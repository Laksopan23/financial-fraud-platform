#!/usr/bin/env python3
"""Exercise the running development stack with synthetic card data. Prints no credentials or PANs."""
from pathlib import Path
import datetime,json,struct,time,urllib.error,urllib.parse,urllib.request,uuid
ROOT=Path(__file__).resolve().parents[1]

def environment():
    return dict(line.split('=',1) for line in (ROOT/'.env').read_text().splitlines() if line and not line.startswith('#'))

def request(url, token=None, payload=None, expected=200, method=None, content_type='application/json'):
    headers={}
    if token: headers['Authorization']='Bearer '+token
    if payload is not None:
        payload=payload if isinstance(payload,bytes) else json.dumps(payload).encode()
        headers['Content-Type']=content_type
    req=urllib.request.Request(url,data=payload,headers=headers,method=method)
    try:
        with urllib.request.urlopen(req,timeout=10) as res: status,body=res.status,res.read()
    except urllib.error.HTTPError as e: status,body=e.code,e.read()
    if status!=expected: raise AssertionError(f'Expected HTTP {expected}, received {status} at {urllib.parse.urlparse(url).path}')
    return json.loads(body) if body else None

def token(client,secret):
    body=urllib.parse.urlencode({'grant_type':'client_credentials','client_id':client,'client_secret':secret}).encode()
    return request('http://localhost:8080/realms/fraud/protocol/openid-connect/token',payload=body,content_type='application/x-www-form-urlencoded')['access_token']

def transaction():
    return {'transactionId':str(uuid.uuid4()),'cardNumber':'4111111111111111','cardholderName':'Synthetic Test',
        'amount':5000,'currency':'USD','merchantId':'high-risk-shop','latitude':0,'longitude':0,
        'occurredAt':datetime.datetime.now(datetime.timezone.utc).isoformat()}

def binary_frame(tx):
    merchant=tx['merchantId'].encode('ascii');pan=tx['cardNumber'].encode('ascii')
    when=int(datetime.datetime.fromisoformat(tx['occurredAt']).timestamp()*1000)
    body=b'\x01'+uuid.UUID(tx['transactionId']).bytes+struct.pack('>q',round(tx['amount']*100))+b'USD'+struct.pack('>qii',when,round(tx['latitude']*1e6),round(tx['longitude']*1e6))
    body+=bytes([len(merchant)])+merchant+bytes([len(pan)])+pan
    return struct.pack('>i',len(body))+body

def main():
    env=environment()
    for port in [8082,8083,8084]: request(f'http://localhost:{port}/health/ready')
    writer=token('transaction-client',env['TRANSACTION_CLIENT_SECRET'])
    auditor=token('audit-client',env['AUDIT_CLIENT_SECRET'])
    other=token('other-client',env['OTHER_CLIENT_SECRET'])
    request('http://localhost:8082/api/transactions',payload=transaction(),expected=401)
    tx=transaction();url='http://localhost:8082/api/transactions'
    first=request(url,writer,tx,202);again=request(url,writer,tx,202)
    assert first==again,'Idempotent retry changed its receipt'
    request(url,writer,{**tx,'amount':5001},409)
    request(url,writer,{**transaction(),'cardNumber':'4111111111111112'},400)
    request(url+'/'+tx['transactionId'],other,expected=404)
    request('http://localhost:8083/api/decisions/'+tx['transactionId'],writer,expected=403)
    request('http://localhost:8083/api/decisions/'+tx['transactionId'],other,expected=404)
    for i in range(24):
        item=transaction();item['longitude']=40 if i%2 else -40
        request(url,writer,item,202)
    binary=transaction()
    request(url+'/binary',writer,binary_frame(binary),202,content_type='application/octet-stream')
    deadline=time.monotonic()+90
    while True:
        try:
            evidence=request('http://localhost:8083/api/decisions/'+tx['transactionId'],auditor)
            break
        except AssertionError:
            if time.monotonic()>deadline: raise
            time.sleep(.5)
    assert evidence['signatureValid'],'Audit signature invalid'
    for shard in range(64):
        result=request(f'http://localhost:8083/api/ledger/verify?shard={shard}&after=0&limit=1000',auditor)
        assert result['valid'],f'Invalid audit shard {shard}'
    print('PASS: authentication, RBAC, tenant isolation, idempotency, validation, binary ingestion and signed audit decisions')
    print('Open http://localhost:8084 and sign in as analyst. Use ANALYST_PASSWORD from .env.')

if __name__=='__main__':main()
