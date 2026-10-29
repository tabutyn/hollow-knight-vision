#!/usr/bin/env python3
"""Summarize LLVM/Coverlet evidence and executable added-line coverage.
Usage: python3 summarize-coverage.py REPO EVIDENCE_DIRECTORY [BASE=ad08a4b]
The evidence directory contains macos-coverage.{json,info}, Windows/receiver
Coverlet exports, and a final source checkout matching those exports.
"""
import json,re,subprocess,sys
from pathlib import Path
repo=Path(sys.argv[1]).resolve(); evidence=Path(sys.argv[2]); base=sys.argv[3] if len(sys.argv)>3 else 'ad08a4b'
def git(*args): return subprocess.check_output(['git','-C',str(repo),*args],text=True)
def relative(filename):
 try: return str(Path(filename).relative_to(repo))
 except ValueError: return filename

def read_lcov(path):
 files={}; current=None
 for line in path.read_text().splitlines():
  if line.startswith('SF:'):
   current=files.setdefault(relative(line[3:]),{})
  elif line.startswith('DA:') and current is not None:
   number,hits,*_=line[3:].split(','); number=int(number)
   current[number]=current.get(number,0)+int(hits)
 return files

line_maps={}
for path in [evidence/'macos-coverage.info',evidence/'windows-coverage-final/coverage.info',evidence/'receiver-coverage/coverage.info']:
 for name,lines in read_lcov(path).items():
  target=line_maps.setdefault(name,{})
  for number,hits in lines.items(): target[number]=target.get(number,0)+hits
prefixes={'macos':'macos/Sources/HollowKnightVision/', 'windows_core':'windows/src/HollowKnightVision.Windows.Core/',
          'windows_app':'windows/src/HollowKnightVision.Windows.App/', 'windows_host':'windows/src/HollowKnightVision.Windows.Host/',
          'shared_receiver':'macos/hollow-knight-input-receiver/src/'}
def counts(lines):
 n=len(lines); c=sum(hits>0 for hits in lines.values())
 return {'covered':c,'count':n,'percent':round(100*c/n,2) if n else None}

llvm=json.loads((evidence/'macos-coverage.json').read_text())['data'][0]['files']
mac_files=[f for f in llvm if relative(f['filename']).startswith(prefixes['macos'])]
llvm_totals={metric:{'covered':sum(f['summary'][metric]['covered'] for f in mac_files),
                    'count':sum(f['summary'][metric]['count'] for f in mac_files)} for metric in ['lines','functions','regions']}
for value in llvm_totals.values(): value['percent']=round(100*value['covered']/value['count'],2)
changes={}; current=None
for line in git('diff','--unified=0',base,'HEAD','--','macos/Sources','macos/hollow-knight-input-receiver/src','windows/src').splitlines():
 if line.startswith('+++ b/'):
  current=line[6:]
  if not current.endswith(('.swift','.cs')): current=None
 elif current and (match:=re.match(r'@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@',line)):
  start=int(match[1]); count=int(match[2] or 1)
  changes.setdefault(current,set()).update(range(start,start+count))
changed_files={}
for name,numbers in changes.items():
 if not numbers: continue
 instrumented={n:h for n,h in line_maps.get(name,{}).items() if n in numbers}
 changed_files[name]={
  'added_or_modified_physical_lines':len(numbers),
  'measured':name in line_maps,
  'changed_executable_lines':counts(instrumented),
  'uncovered_changed_executable_lines':[n for n,h in sorted(instrumented.items()) if h==0],
 }
groups={}
for group,prefix in prefixes.items():
 files={name:counts(lines) for name,lines in sorted(line_maps.items()) if name.startswith(prefix)}
 changed={name:value for name,value in changed_files.items() if name.startswith(prefix)}
 totals={'covered':sum(value['covered'] for value in files.values()),'count':sum(value['count'] for value in files.values())}
 totals['percent']=round(100*totals['covered']/totals['count'],2) if totals['count'] else None
 added={'covered':sum(v['changed_executable_lines']['covered'] for v in changed.values()),'count':sum(v['changed_executable_lines']['count'] for v in changed.values())}
 added['percent']=round(100*added['covered']/added['count'],2) if added['count'] else None
 groups[group]={'unique_executable_line_coverage':totals,'changed_executable_line_coverage':added,
  'unmeasured_changed_files':[name for name,value in changed.items() if not value['measured']], 'files':files}
result={'base':git('rev-parse',base).strip(),'source_revision':git('rev-parse','HEAD').strip(),
 'scope':'Production changes since ad08a4b; experimental Step 1s is inventoried but not ported to Mac.',
 'macos_llvm_summary':llvm_totals,'groups':groups,'changed_files':dict(sorted(changed_files.items())),
 'limitations':['Changed-line percentages only include instrumented executable lines. Unmeasured files are listed separately; they are not assumed covered.',
 'Swift LLVM summary counts may include compiler-instantiated line entries; unique LCOV line counts are reported separately.',
 'Windows measurements were collected on macOS, with five remaining Windows filesystem-semantics failures and 14 skipped checks. This is not native Windows UI/CUDA validation.',
 'Unity runtime receiver hooks, Metal shader instructions, native GUI/input/capture wiring and the trainer are not fully covered by these harnesses.']}
mac_log=(evidence/'macos-final-coverage-tests.log').read_text()
cases,skipped,failures=map(int,re.findall(r'Executed (\d+) tests, with (\d+) tests skipped and (\d+) failures',mac_log)[-1])
swift_passed=int(re.findall(r'Test run with (\d+) tests? in',mac_log)[-1])
win_log=(evidence/'windows-latest-coverage-tests.log').read_text()
retry_log=(evidence/'windows-resource-tests.log').read_text()
win_count=int(re.findall(r'AUDIT total=(\d+)',win_log)[-1])
failed_names=set(re.findall(r'^FAIL (.*?): ',win_log,re.M))
failed_names.difference_update(re.findall(r'^PASS (.*)$',retry_log,re.M))
win_skipped=len(re.findall(r'^SKIP ',win_log,re.M))
python_passed=sum(int(re.findall(r'Ran (\d+) tests?',path.read_text())[-1]) for path in evidence.glob('python-[0-4].log'))
result['tests']={'macos_xctest_cases':cases,'macos_xctest_passed':cases-skipped-failures,
 'macos_xctest_skipped':skipped,'macos_swift_testing_passed':swift_passed,'macos_failures':failures,
 'macos_excluded_corpus_class_cases':len(re.findall(r'    func test', (repo/'macos/Tests/HollowKnightVisionTests/MenuStencilTests.swift').read_text())),
 'python_passed':python_passed,'windows_registered_cases':win_count,'windows_passed':win_count-win_skipped-len(failed_names),
 'windows_skipped':win_skipped,'windows_host_semantics_failures':len(failed_names)}
(evidence/'report/coverage-summary.json').write_text(json.dumps(result,indent=2)+'\n')
for group,value in groups.items(): print(group,value['unique_executable_line_coverage'], 'changed',value['changed_executable_line_coverage'],'unmeasured files',len(value['unmeasured_changed_files']))
