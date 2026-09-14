#!/usr/bin/env python3
"""Tests development generators/encoders without pretending to run the .NET services."""
import base64,contextlib,http.server,importlib.util,io,json,os,struct,subprocess,tempfile,threading,unittest,uuid
from pathlib import Path
import yaml
ROOT=Path(__file__).resolve().parents[1]
def module(name):
    spec=importlib.util.spec_from_file_location(name,ROOT/'scripts'/f'{name}.py')
    value=importlib.util.module_from_spec(spec);spec.loader.exec_module(value);return value
setup=module('dev_setup');smoke=module('smoke')

class DevToolsTests(unittest.TestCase):
    def test_generator_uses_unique_secrets_and_independent_256_bit_keys(self):
        with tempfile.TemporaryDirectory() as tmp,contextlib.redirect_stdout(io.StringIO()):
            values,realm=setup.create(Path(tmp))
            self.assertEqual(len(values.values()),len(set(values.values())))
            for key in ['ENCRYPTION_KEY','TOKENIZATION_KEY','AUDIT_KEY']:
                self.assertEqual(32,len(base64.b64decode(values[key])))
            self.assertTrue(realm['bruteForceProtected'])
            self.assertFalse(realm['registrationAllowed'])
            if os.name=='posix':self.assertEqual(0o600,(Path(tmp)/'.env').stat().st_mode&0o777)
    def test_generator_refuses_to_overwrite_existing_keys(self):
        with tempfile.TemporaryDirectory() as tmp,contextlib.redirect_stdout(io.StringIO()):
            path=Path(tmp);setup.create(path);before=(path/'.env').read_bytes()
            with self.assertRaises(SystemExit):setup.create(path)
            self.assertEqual(before,(path/'.env').read_bytes())
    def test_keycloak_clients_have_explicit_role_and_tenant_claims(self):
        with tempfile.TemporaryDirectory() as tmp,contextlib.redirect_stdout(io.StringIO()):
            values,realm=setup.create(Path(tmp))
            for client in realm['clients']:
                self.assertFalse(client['directAccessGrantsEnabled'])
                claims={m['config'].get('claim.name') for m in client['protocolMappers']}
                self.assertTrue({'tenant','roles'}.issubset(claims))
            other=next(u for u in realm['users'] if u.get('serviceAccountClientId')=='other-client')
            self.assertEqual(['other'],other['attributes']['tenant'])
            dashboard=next(c for c in realm['clients'] if c['clientId']=='fraud-dashboard')
            self.assertEqual('S256',dashboard['attributes']['pkce.code.challenge.method'])
    def test_compose_credentials_are_all_generated(self):
        with tempfile.TemporaryDirectory() as tmp,contextlib.redirect_stdout(io.StringIO()):
            values,_=setup.create(Path(tmp))
            import re
            used=set(re.findall(r'\$\{([A-Z_]+):\?',(ROOT/'docker-compose.yml').read_text()))
            self.assertTrue(used.issubset(values.keys()),used-set(values.keys()))
    def test_binary_encoder_has_correct_network_order_and_boundaries(self):
        tx=smoke.transaction();frame=smoke.binary_frame(tx)
        length=struct.unpack('>i',frame[:4])[0]
        self.assertEqual(length,len(frame)-4);self.assertLessEqual(length,256)
        body=frame[4:];self.assertEqual(1,body[0]);self.assertEqual(uuid.UUID(tx['transactionId']).bytes,body[1:17])
        self.assertEqual(500000,struct.unpack('>q',body[17:25])[0]);self.assertEqual(b'USD',body[25:28])
        merchant_length=body[44];position=45+merchant_length
        pan_length=body[position];self.assertEqual(16,pan_length)
        self.assertEqual(position+1+pan_length,len(body))
    def test_keycloak_health_command_handles_ready_and_unready_status(self):
        compose=yaml.safe_load((ROOT/'docker-compose.yml').read_text())
        command=compose['services']['keycloak']['healthcheck']['test'][3].replace('$$','$')
        class Handler(http.server.BaseHTTPRequestHandler):
            status=200
            def do_GET(self):
                self.send_response(type(self).status);self.end_headers()
            def log_message(self,*args):pass
        server=http.server.HTTPServer(('127.0.0.1',0),Handler)
        thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
        try:
            code=command.replace('/127.0.0.1/9000','/127.0.0.1/'+str(server.server_port))
            self.assertEqual(0,subprocess.run(['bash','-ec',code],capture_output=True,timeout=5).returncode)
            Handler.status=503
            self.assertNotEqual(0,subprocess.run(['bash','-ec',code],capture_output=True,timeout=5).returncode)
        finally:server.shutdown();server.server_close();thread.join(timeout=2)

if __name__=='__main__':unittest.main()
