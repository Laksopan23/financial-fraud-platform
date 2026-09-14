#!/usr/bin/env python3
"""Static structure validation. This is not a C# compiler or a Docker runtime test."""
from pathlib import Path
import ast,importlib.util,json,re,xml.etree.ElementTree as ET
import yaml
ROOT=Path(__file__).resolve().parents[1]

def main():
    checks=[]
    def check(condition,name):
        if not condition: raise AssertionError(name)
        checks.append(name)
    projects=list(ROOT.rglob('*.csproj'))
    central=ET.parse(ROOT/'Directory.Packages.props').getroot()
    packages={n.attrib['Include'] for n in central.findall('.//PackageVersion')}
    dependencies={}
    for project in projects:
        tree=ET.parse(project).getroot()
        refs=[]
        for node in tree.findall('.//ProjectReference'):
            target=(project.parent/node.attrib['Include']).resolve()
            check(target.is_file(),'Project reference: '+project.stem+' -> '+target.stem)
            refs.append(target)
        for node in tree.findall('.//PackageReference'):
            check(node.attrib['Include'] in packages,'Central package: '+node.attrib['Include'])
        for element in ['Protobuf','EmbeddedResource','Compile']:
            for node in tree.findall('.//'+element):
                check((project.parent/node.attrib['Include']).is_file(),'Build input: '+node.attrib['Include'])
        dependencies[project.resolve()]=refs
    def acyclic(graph):
        seen=set();active=set()
        def visit(node):
            if node in active: raise AssertionError('Dependency cycle')
            if node in seen:return
            active.add(node)
            for child in graph.get(node,[]):visit(child)
            active.remove(node);seen.add(node)
        for node in graph:visit(node)
    acyclic(dependencies);checks.append('Project graph has no dependency cycles')
    domain=next(p for p in projects if p.stem.endswith('Ingestion.Domain'))
    check(all(p.stem.endswith('.Core') for p in dependencies[domain.resolve()]),'Domain references only Core')
    check(not ET.parse(domain).getroot().findall('.//PackageReference'),'Domain has no framework packages')
    solution=(ROOT/'FinancialFraudPlatform.sln').read_text()
    check(solution.count('EndProject\n')==len(projects),'Solution includes every project')
    for project in projects:
        check(str(project.relative_to(ROOT)).replace('/','\\') in solution,'Solution path: '+project.stem)
    for path in ROOT.rglob('*.json'):json.loads(path.read_text())
    for path in list(ROOT.rglob('*.yml'))+list(ROOT.rglob('*.yaml')):yaml.safe_load(path.read_text())
    checks.append('JSON and YAML documents parse')
    compose=yaml.safe_load((ROOT/'docker-compose.yml').read_text());services=compose['services']
    acyclic({name:list(svc.get('depends_on',{})) for name,svc in services.items()})
    checks.append('Compose startup graph has no dependency cycles')
    for name,svc in services.items():
        for dependency in svc.get('depends_on',{}):check(dependency in services,'Compose dependency: '+name+' -> '+dependency)
        for network in svc.get('networks',[]):check(network in compose['networks'],'Compose network: '+name+' -> '+network)
        for port in svc.get('ports',[]):check(port.startswith('127.0.0.1:'),'Loopback-only published port: '+name)
        if 'build' in svc:
            check((ROOT/svc['build']['args']['PROJECT']).is_file(),'Container project: '+name)
        for mount in svc.get('volumes',[]):
            source=mount.split(':',1)[0]
            if source.startswith('./') and '/keycloak/generated/' not in source:
                check((ROOT/source).exists(),'Bind mount: '+source)
    for path in ROOT.rglob('*.py'):ast.parse(path.read_text(),filename=str(path))
    checks.append('Python scripts parse')
    for path in ROOT.rglob('*.md'):
        for link in re.findall(r'\]\(([^)]+)\)',path.read_text()):
            if '://' in link or link.startswith('#'):continue
            target=link.split('#',1)[0]
            check((path.parent/target).exists(),'Documentation link: '+path.name+' -> '+target)
    code='\n'.join(p.read_text() for p in ROOT.rglob('*.cs'))
    check(not re.search(r'//\s*TODO\b|throw\s+new\s+NotImplementedException',code),'No unimplemented C# stubs')
    check(not (ROOT/'.env').exists(),'No generated secrets in deliverable source')
    report={'kind':'static-structure-only','passed':len(checks),'projects':len(projects),
        'csharpFiles':len(list(ROOT.rglob('*.cs'))),'composeServices':len(services),'checks':checks}
    (ROOT/'docs/structure-validation.json').write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps({k:v for k,v in report.items() if k!='checks'}))

if __name__=='__main__':main()
