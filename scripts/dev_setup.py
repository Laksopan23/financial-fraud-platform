#!/usr/bin/env python3
"""Create isolated development credentials and a Keycloak realm. Never prints secrets."""
from pathlib import Path
import base64,json,os,secrets

ROOT = Path(__file__).resolve().parents[1]

def create(root=ROOT):
    env_path = root / '.env'
    realm_path = root / 'infra/keycloak/generated/fraud-realm.json'
    if env_path.exists() or realm_path.exists():
        raise SystemExit('Development configuration already exists. Reuse it; do not rotate credentials underneath existing volumes.')
    values = {name: secrets.token_hex(24) for name in [
        'POSTGRES_PASSWORD','INGESTION_DB_PASSWORD','DETECTION_DB_PASSWORD','AUDIT_DB_PASSWORD',
        'DASHBOARD_DB_PASSWORD','KEYCLOAK_DB_PASSWORD','RABBITMQ_PASSWORD','REDIS_PASSWORD',
        'KEYCLOAK_ADMIN_PASSWORD','DASHBOARD_CLIENT_SECRET','TRANSACTION_CLIENT_SECRET',
        'AUDIT_CLIENT_SECRET','OTHER_CLIENT_SECRET','ANALYST_PASSWORD','GRAFANA_PASSWORD']}
    values.update({name:base64.b64encode(secrets.token_bytes(32)).decode() for name in ['ENCRYPTION_KEY','TOKENIZATION_KEY','AUDIT_KEY']})
    def mapper(name, protocol, config):
        return {'name':name,'protocol':'openid-connect','protocolMapper':protocol,'consentRequired':False,'config':config}
    mappers = [
        mapper('roles','oidc-usermodel-realm-role-mapper',{'claim.name':'roles','multivalued':'true','jsonType.label':'String','id.token.claim':'true','access.token.claim':'true','userinfo.token.claim':'false'}),
        mapper('tenant','oidc-usermodel-attribute-mapper',{'user.attribute':'tenant','claim.name':'tenant','jsonType.label':'String','id.token.claim':'true','access.token.claim':'true','multivalued':'false'}),
        mapper('audience','oidc-audience-mapper',{'included.custom.audience':'fraud-platform','id.token.claim':'false','access.token.claim':'true'})]
    def service_client(name, secret):
        return {'clientId':name,'secret':secret,'enabled':True,'protocol':'openid-connect','publicClient':False,
            'serviceAccountsEnabled':True,'standardFlowEnabled':False,'directAccessGrantsEnabled':False,
            'fullScopeAllowed':True,'protocolMappers':mappers}
    def account(client, roles, tenant="demo"):
        return {'username':'service-account-'+client,'enabled':True,'serviceAccountClientId':client,
            'attributes':{'tenant':[tenant]},'realmRoles':roles}
    realm = {'realm':'fraud','enabled':True,'sslRequired':'none','registrationAllowed':False,
        'resetPasswordAllowed':False,'bruteForceProtected':True,'accessTokenLifespan':300,
        'ssoSessionIdleTimeout':900,'ssoSessionMaxLifespan':3600,
        'roles':{'realm':[{'name':name} for name in ['transaction_writer','analyst','auditor']]},
        'clients':[
            service_client('transaction-client',values['TRANSACTION_CLIENT_SECRET']),
            service_client('audit-client',values['AUDIT_CLIENT_SECRET']),
            service_client('other-client',values['OTHER_CLIENT_SECRET']),
            {'clientId':'fraud-dashboard','enabled':True,'protocol':'openid-connect','publicClient':False,
             'secret':values['DASHBOARD_CLIENT_SECRET'],'standardFlowEnabled':True,
             'directAccessGrantsEnabled':False,'serviceAccountsEnabled':False,
             'redirectUris':['http://localhost:8084/signin-oidc'], 'webOrigins':['http://localhost:8084'],
             'attributes':{'pkce.code.challenge.method':'S256'},'protocolMappers':mappers}],
        'users':[
            account('transaction-client',['transaction_writer']),account('audit-client',['auditor']),
            account('other-client',['transaction_writer','auditor'],'other'),
            {'username':'analyst','enabled':True,'emailVerified':True,'firstName':'Demo','lastName':'Analyst',
             'attributes':{'tenant':['demo']},'realmRoles':['analyst','auditor'],
             'credentials':[{'type':'password','value':values['ANALYST_PASSWORD'],'temporary':False}]}]}
    realm_path.parent.mkdir(parents=True,exist_ok=True)
    env_path.write_text('\n'.join(k+'='+v for k,v in values.items())+'\n')
    realm_path.write_text(json.dumps(realm,indent=2)+'\n')
    # The realm is read by the unprivileged Keycloak container; the parent directory is private.
    try:
        os.chmod(env_path,0o600)
        os.chmod(realm_path.parent,0o700)
        os.chmod(realm_path,0o644)
    except OSError:
        pass
    print('Created development configuration. Credentials are in .env; none were printed.')
    return values, realm

if __name__ == '__main__': create()
