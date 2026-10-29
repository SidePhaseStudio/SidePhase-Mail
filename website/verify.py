from pathlib import Path
from html.parser import HTMLParser
from urllib.parse import urlsplit
import xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parent
BASE='https://sidephasestudio.com/sidephasemail/'
class Document(HTMLParser):
    def __init__(self):
        super().__init__(); self.links=[]; self.h1=0; self.ids=set(); self.canonical=None
    def handle_starttag(self,tag,attrs):
        a=dict(attrs)
        if tag=='h1': self.h1+=1
        if 'id' in a:
            assert a['id'] not in self.ids, 'duplicate id'
            self.ids.add(a['id'])
        if tag=='img': assert 'alt' in a, 'missing image alternative'
        if tag=='link' and a.get('rel')=='canonical': self.canonical=a.get('href')
        assert tag not in ('script','iframe','form'), 'unexpected tracking/interactive surface'
        for key in ('href','src'):
            if key in a:self.links.append(a[key])
for file in sorted((ROOT/'sidephasemail').rglob('*.html')):
    d=Document();d.feed(file.read_text(encoding='utf-8'))
    assert d.h1==1, (file,'one main heading required')
    relative=file.relative_to(ROOT/'sidephasemail').as_posix().removesuffix('index.html')
    assert d.canonical==BASE+relative, (file,'canonical URL')
    for link in d.links:
        u=urlsplit(link)
        if u.scheme or u.netloc:continue
        if u.path.startswith('/sidephasemail/'):
            target=ROOT/u.path.lstrip('/')
            if target.is_dir(): target/='index.html'
            assert target.is_file(), (file,link)
        if not u.path and u.fragment:assert u.fragment in d.ids
    print('PASS',file.relative_to(ROOT))
privacy=(ROOT/'sidephasemail/privacy/index.html').read_text(encoding='utf-8')
for disclosure in ['Limited Use','DPAPI','not encrypted','outbox','backups','support@sidephasestudio.com']:
    assert disclosure in privacy, disclosure
for view in ['WelcomePage','AddAccountPage','SettingsPage']:
    file=ROOT.parent/'src/SidePhase.Mail.App/Views'/f'{view}.xaml'
    ET.parse(file)
    text=file.read_text(encoding='utf-8')
    assert BASE+'privacy/' in text, (view,'missing policy link')
print('PASS policy disclosures and in-app privacy links')
