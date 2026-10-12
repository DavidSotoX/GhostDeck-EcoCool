#!/usr/bin/env python3
"""Build and verify the canonical local delivery from fixed input bytes; never touches hardware."""
from pathlib import Path
import hashlib,json,zipfile,os
ROOT=Path(__file__).resolve().parents[1]
VERSION='1.39.2'
MANIFEST=ROOT/'reports/CANONICAL_MANIFEST_1392.json'
ARCHIVE=ROOT/f'releases/GhostDeck-EcoCool-{VERSION}-canonical.zip'
EXCLUDED={'bin','obj','.git','.vs','.idea','__pycache__'}
BASELINES={'releases/baseline-1.39.1.zip','releases/baseline-1.39.1.manifest.json'}
def sha(data):return hashlib.sha256(data).hexdigest()
def json_bytes(data):return (json.dumps(data,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
def inputs():
    out=[]
    for p in ROOT.rglob('*'):
        if not p.is_file():continue
        rel=p.relative_to(ROOT)
        if EXCLUDED.intersection(rel.parts):continue
        if rel.parts[0]=='releases' and rel.as_posix() not in BASELINES:continue
        if rel.parts[0]=='dist' and rel.parts[1]!='win-x64':continue
        if p==MANIFEST or p.suffix in {'.pyc','.tmp'}:continue
        out.append(p)
    return sorted(out,key=lambda p:p.relative_to(ROOT).as_posix())
def classify(name):
    if name in BASELINES:return 'rollback-baseline-1.39.1'
    if name.startswith('dist/'):return 'active-derived-distribution'
    if name.startswith('reports/hardware-1.39.1/'):return 'historical-hardware-evidence'
    if name.startswith('scripts/'):return 'legacy-standalone-not-hardware-authority'
    if name.startswith('.github/'):return 'automation-config-not-run-remotely'
    if name.startswith('reports/'):
        return 'current-evidence' if '1392' in name or name.endswith(('VV_STATUS.md','STATIC_VV.txt','LANG_VV.txt')) else 'historical-reference'
    return 'canonical-source-or-resource'
def main():
    ARCHIVE.parent.mkdir(exist_ok=True)
    for rel in BASELINES:
        if not (ROOT/rel).is_file():raise RuntimeError('Missing recovery artifact '+rel)
    baseline=json.loads((ROOT/'releases/baseline-1.39.1.manifest.json').read_text(encoding='utf-8'))
    old={r['path']:r['sha256'] for r in baseline}
    source=[p for p in inputs() if p.relative_to(ROOT).parts[0] not in {'dist','reports','releases'}]
    rows=[]
    for p in source:
        name=p.relative_to(ROOT).as_posix();digest=sha(p.read_bytes())
        if old.get(name)!=digest:rows.append({'path':name,'status':'modified' if name in old else 'added','before':old.get(name),'after':digest})
    (ROOT/'reports/CHANGES_1392.json').write_bytes(json_bytes(rows))
    content={p.relative_to(ROOT).as_posix():p.read_bytes() for p in inputs()}
    records=[{'path':name,'bytes':len(data),'sha256':sha(data),'classification':classify(name)} for name,data in content.items()]
    manifest={'schema':1,'version':VERSION,'status':'SOFTWARE_VERIFIED_HARDWARE_LOCKED_FINAL_NOT_PROVEN','files':records}
    manifest_data=json_bytes(manifest);MANIFEST.write_bytes(manifest_data)
    content[MANIFEST.relative_to(ROOT).as_posix()]=manifest_data
    temporary=ARCHIVE.with_suffix('.zip.tmp')
    if temporary.exists():raise RuntimeError('Unclassified partial archive exists: '+str(temporary))
    try:
        with zipfile.ZipFile(temporary,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as z:
            for name,data in sorted(content.items()):
                info=zipfile.ZipInfo(name,date_time=(2026,10,8,0,0,0));info.compress_type=zipfile.ZIP_DEFLATED
                info.external_attr=0o100644<<16
                z.writestr(info,data,compresslevel=9)
        with zipfile.ZipFile(temporary) as z:
            if z.testzip() is not None:raise RuntimeError('ZIP CRC check failed')
            if set(z.namelist())!=set(content):raise RuntimeError('ZIP file inventory mismatch')
            for record in records:
                if sha(z.read(record['path']))!=record['sha256']:raise RuntimeError('ZIP SHA256 mismatch: '+record['path'])
        os.replace(temporary,ARCHIVE)
    finally:
        if temporary.exists():temporary.unlink()
    digest=sha(ARCHIVE.read_bytes())
    ARCHIVE.with_suffix('.zip.sha256').write_text(digest+'  '+ARCHIVE.name+'\n',encoding='ascii')
    print(f'PASS canonical ZIP CRC, inventory and {len(records)} SHA256 records')
    print(f'Source changes: {len(rows)}; archive bytes: {ARCHIVE.stat().st_size}')
    print(f'ZIP: {ARCHIVE.relative_to(ROOT).as_posix()}')
    print(f'SHA256: {digest}')
if __name__=='__main__':main()
