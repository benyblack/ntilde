"""Verify retained Git commit/tree bindings, bundle, patch and selected source bytes offline."""
import base64
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import zipfile
import zlib

source_path, proof_path, bundle_path, patch_path, base, head, metadata_path, git = sys.argv[1:]
if not os.path.isabs(git):
    raise ValueError("Git must have an explicit absolute installation path")

def git_hash(kind, data):
    """Compute the Git object identifier required by the retained repository format."""
    return hashlib.sha1(kind.encode()+b" "+str(len(data)).encode()+b"\0"+data).hexdigest()

def read_zip(path):
    """Refuse unsafe names and bound the complete source expansion."""
    archive = zipfile.ZipFile(path)
    names = archive.namelist()
    if len(names) != len(set(names)) or any(n.startswith('/') or '..' in n.split('/') or chr(92) in n for n in names):
        raise ValueError("unsafe or duplicate retained source member")
    if sum(i.file_size for i in archive.infolist()) > 128*1024*1024:
        raise ValueError("source proof exceeds its expansion budget")
    return archive

with read_zip(proof_path) as proof, read_zip(source_path) as source:
    index = json.loads(proof.read('index.json'))
    if index['base'] != base or index['head'] != head:
        raise ValueError('source object proof names another revision')
    objects = {}
    for entry in index['objects']:
        oid, kind = entry['oid'], entry['type']
        if not re.fullmatch('[a-f0-9]{40}', oid) or kind not in ['commit','tree','blob'] or oid in objects:
            raise ValueError('invalid source object identity')
        data = proof.read('objects/'+oid)
        if len(data) != entry['size'] or git_hash(kind,data) != oid:
            raise ValueError('source object does not match its Git identity')
        objects[oid] = (kind,data)
    def root(commit):
        """Read the root tree from a digest-verified commit object."""
        kind, data = objects[commit]
        if kind != 'commit': raise ValueError('expected commit object')
        line = data.split(b'\n',1)[0]
        if not line.startswith(b'tree '): raise ValueError('commit has no tree')
        return line[5:].decode()
    def tree_entries(oid):
        """Decode the names, modes and object identities in a verified Git tree."""
        kind, data = objects[oid]
        if kind != 'tree': raise ValueError('expected tree object')
        entries = {}; position = 0
        while position < len(data):
            zero = data.index(b'\0',position)
            mode,name = data[position:zero].split(b' ',1)
            child = data[zero+1:zero+21]
            if len(child) != 20 or name.decode() in entries: raise ValueError('invalid Git tree')
            entries[name.decode()] = (mode.decode(),child.hex()); position = zero+21
        return entries
    def resolve(commit,path):
        """Follow a committed path through its content-addressed tree chain."""
        oid = root(commit); mode = '40000'
        for part in path.split('/'):
            mode,oid = tree_entries(oid)[part]
        return mode,oid
    for path in ['packages/probe-defs','packages/probes']:
        if resolve(base,path) != resolve(head,path):
            raise ValueError('upstream probe definitions or graders changed')
    files = {}
    for name in source.namelist():
        if name.endswith('/'): continue
        mode,oid = resolve(head,name); data = source.read(name)
        if mode not in ['100644','100755','120000'] or git_hash('blob',data) != oid:
            raise ValueError('source snapshot differs from committed bytes: '+name)
        files[name] = data
    if source.comment.decode().strip() != head:
        raise ValueError('source archive does not name its measured Git commit')
    with tempfile.TemporaryDirectory(prefix='common-core-git-') as directory:
        environment = dict(os.environ, GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull)
        def command(*args):
            """Run only the explicit Git installation in the isolated proof repository."""
            return subprocess.check_output([git,'-C',directory,*args],env=environment,stderr=subprocess.PIPE,timeout=30)
        command('init','--bare','--quiet')
        for oid,(kind,data) in objects.items():
            path = Path(directory)/'objects'/oid[:2]/oid[2:]; path.parent.mkdir(parents=True,exist_ok=True)
            path.write_bytes(zlib.compress(kind.encode()+b' '+str(len(data)).encode()+b'\0'+data))
        ref = Path(directory)/'refs/heads/base';ref.parent.mkdir(parents=True,exist_ok=True);ref.write_text(base+'\n')
        command('bundle','verify',str(Path(bundle_path).resolve()))
        command('bundle','unbundle',str(Path(bundle_path).resolve()))
        cursor = head
        for step in range(100):
            if cursor == base: break
            commit = command('cat-file','commit',cursor)
            parents = [line[7:].decode() for line in commit.split(b'\n') if line.startswith(b'parent ')]
            if len(parents) != 1: raise ValueError('adapter history is not the retained linear source stack')
            cursor = parents[0]
        if cursor != base: raise ValueError('adapter bundle does not reproduce its source ancestry')
        advertised = command('bundle','list-heads',str(Path(bundle_path).resolve())).decode().splitlines()
        if advertised != [head+' HEAD']: raise ValueError('bundle advertises a different source head')
        difference = command('-c','core.quotePath=true','diff','--binary','--full-index','--no-ext-diff','--no-textconv','--no-renames','--diff-algorithm=myers',base,head)
        patch = gzip.decompress(Path(patch_path).read_bytes())
        if difference != patch: raise ValueError('adapter patch differs from retained Git trees')
    metadata = json.loads(Path(metadata_path).read_text())
    wanted = set(metadata['suite']['sourcePaths']) | {'content/features.json','packages/admin/versions.ts','packages/terminfo.dev/package.json'}
    if not wanted.issubset(files):raise ValueError('retained source omits a suite input')
    print(json.dumps({'paths':list(files),'files':{name:base64.b64encode(files[name]).decode() for name in wanted},'base':base,'head':head}))
