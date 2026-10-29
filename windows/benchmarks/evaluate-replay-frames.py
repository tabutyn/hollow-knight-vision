"""Score a live replay against frame-ID-matched receiver telemetry.

Usage: python evaluate-replay-frames.py SOURCE REPLAY FRAMES OUTPUT_DIRECTORY
Requires numpy. FRAMES is the installed app's benchmark frames.json.
No spatial fit, endpoint correction, or interpolated tracker positions are used.
"""
import argparse, bisect, collections, csv, hashlib, json, math
from pathlib import Path
import numpy as np

def stats(values):
    values = np.array([v for v in values if v is not None and math.isfinite(v)], dtype=float)
    if not len(values): return None
    return dict(count=len(values), mean=float(values.mean()), **{n:float(np.percentile(values,p)) for n,p in [('median',50),('p95',95),('p99',99),('max',100)]})

def write_csv(path, rows):
    if not rows: return
    with path.open('w',newline='') as f:
        w=csv.DictWriter(f,fieldnames=list(rows[0]));w.writeheader();w.writerows(rows)

def camera(sample,w=640,h=360):
    return np.array([sample['cameraX']*sample['pixelsPerWorldUnitX']*w/sample['projectionPixelWidth'], sample['cameraY']*sample['pixelsPerWorldUnitY']*h/sample['projectionPixelHeight']])

def evaluate(source_file, replay_file, audit_file, output):
    source=json.loads(source_file.read_text(encoding='utf-8-sig'))
    path=json.loads(replay_file.read_text(encoding='utf-8-sig'))
    audit=json.loads(audit_file.read_text(encoding='utf-8-sig'))
    truth=path['groundTruthSamples']; by_key={r['sample']['unityFrame']&0xffffff:r for r in truth}
    rows=[{k[0].lower()+k[1:]:v for k,v in s.items()} for s in audit['samples']]
    rows=[r for r in rows if audit['startedAt'] <= r['observedAt'] <= audit['startedAt']+path['duration']]
    captures=[r for r in rows if r['kind']=='capture']; present=sorted([r for r in rows if r['kind']=='present'],key=lambda s:s['observedAt'])
    mode=audit['workspace']; tracking=path['trackingSamples']; solver=[]; baseline=None
    if len({r['sample']['sceneName'] for r in truth}) != 1:
        raise ValueError('This evaluator requires one scene; cross-scene camera origins need separate registration.')
    if not truth or not tracking: raise ValueError('Replay requires receiver telemetry and tracking samples.')
    for r in tracking:
        gt=by_key.get(r.get('renderedGameFrame'))
        if gt is None or r.get('publishedCameraX') is None: continue
        expected=camera(gt['sample']); actual=np.array([r['publishedCameraX'],r['publishedCameraY']])
        if baseline is None: baseline=actual-expected
        delta=actual-baseline-expected
        solver.append(dict(offset=r['offset'],frame=r['renderedGameFrame'],error=float(np.linalg.norm(delta)),errorX=float(delta[0]),errorY=float(delta[1]),verified=r['poseVerified'],source=r['poseSource']))
    presented=[]; display_baseline=None; previous_key=None; unique_display=[]
    for r in present:
        gt=by_key.get(r['gameFrame']); error=None; ex=None; ey=None
        valid=bool(r['registered'] and gt is not None and r['cameraX'] is not None and r['cameraY'] is not None)
        if valid:
            expected=camera(gt['sample'],r['width'],r['height']); actual=np.array([r['cameraX'],r['cameraY']])
            if display_baseline is None: display_baseline=np.zeros(2) if mode=='Hacker' else actual-expected
            delta=actual-display_baseline-expected;ex=float(delta[0]);ey=float(delta[1]);error=float(np.linalg.norm(delta))
        row=dict(offset=r['observedAt']-audit['startedAt'],frame=r['gameFrame'],captureId=r['captureId'],capturedAt=r['capturedAt'],observedAt=r['observedAt'],registered=r['registered'],matched=gt is not None,error=error,errorX=ex,errorY=ey,ageMs=(r['observedAt']-r['capturedAt'])*1000,cameraX=r['cameraX'],cameraY=r['cameraY'])
        presented.append(row)
        if r['gameFrame'] != previous_key:
            unique_display.append(row);previous_key=r['gameFrame']
    # Evaluate actual held display state at fixed 30 Hz deadlines. No interpolation
    # of tracker poses and no treating an absent frame as a zero-error result.
    display_times=[r['observedAt'] for r in presented];truth_times=[r['receivedTimestamp'] for r in truth];ticks=[];previous_key=None
    for offset in np.arange(0,path['duration'],1/30):
        now=audit['startedAt']+float(offset);index=bisect.bisect_right(display_times,now)-1
        r=presented[index] if index>=0 else None
        current=bisect.bisect_right(truth_times,now)-1; current=truth[current] if current>=0 else None
        valid=r is not None and r['error'] is not None and r['registered']
        age=(now-r['capturedAt'])*1000 if r else None
        reused=r is not None and r['frame']==previous_key
        lag=current['sample']['unityFrame']-(r['frame'] or 0) if current and r else None
        if lag is not None: lag=((lag+2**23)%2**24)-2**23
        current_error=None
        if valid and current is not None:
            current_error=float(np.linalg.norm(np.array([r['cameraX'],r['cameraY']])-display_baseline-camera(current['sample'])))
        ticks.append(dict(offset=float(offset),frame=r['frame'] if r else None,valid=valid,reused=reused,ageMs=age,gameFrameLag=lag,error=r['error'] if valid else None,errorVersusCurrentGame=current_error,within2px=bool(valid and r['error']<=2),within8px=bool(valid and r['error']<=8),freshAndWithin2px=bool(valid and r['error']<=2 and age<=1000/30 and not reused)))
        if r: previous_key=r['frame']
    capkeys={r['gameFrame'] for r in captures if r['gameFrame'] in by_key};analysiskeys={r['frame'] for r in solver};presentkeys={r['frame'] for r in presented if r['matched']}
    analysis_by_key={r['frame']:r for r in solver};present_by_key=collections.defaultdict(list)
    for r in presented:
        if r['matched']:present_by_key[r['frame']].append(r)
    every_frame=[]
    for gt in truth:
        key=gt['sample']['unityFrame']&0xffffff; a=analysis_by_key.get(key);pr=present_by_key[key]
        every_frame.append(dict(offset=gt['offset'],unityFrame=gt['sample']['unityFrame'],captured=key in capkeys,analyzed=key in analysiskeys,presented=key in presentkeys,solverError=a['error'] if a else None,displayErrorMin=min((x['error'] for x in pr if x['error'] is not None),default=None),displayErrorMax=max((x['error'] for x in pr if x['error'] is not None),default=None)))
    st=np.array([r['offset'] for r in source['groundTruthSamples']]);rt=np.array([r['offset'] for r in truth]);hero_delta=[];cam_delta=[]
    for fields,target in [(('heroX','heroY'),hero_delta),(('cameraX','cameraY'),cam_delta)]:
        delta=[np.array([r['sample'][f] for r in truth])-np.interp(rt,st,[r['sample'][f] for r in source['groundTruthSamples']]) for f in fields]
        target.extend(np.hypot(delta[0],delta[1]).tolist())
    end_source=source['groundTruthSamples'][-1]['sample'];end=truth[-1]['sample'];start=source['groundTruthSamples'][0]['sample']
    same_events=len(source['events'])==len(path['events']) and all((a['button'],a['transition'])==(b['button'],b['transition']) for a,b in zip(source['events'],path['events']))
    intervals=lambda rs,field:[(b[field]-a[field])*1000 for a,b in zip(rs,rs[1:])]
    report=dict(workspace=mode,sourceId=source['id'],replayId=path['id'],duration=path['duration'],sourceSha256=hashlib.sha256(source_file.read_bytes()).hexdigest(),auditOmitted=audit['omitted'],
        definition='Errors in 640x360 solve pixels. Gameplay subtracts ONE initial translation. Hacker uses absolute camera coordinates reconstructed from image placement. Missing frames remain missing. Rendering event measures WPF publication, not physical monitor scanout.',
        sameInputs=same_events,inputEvents=len(path['events']),inputTimingErrorSeconds=stats([abs(a['offset']-b['offset']) for a,b in zip(source['events'],path['events'])]) if same_events else None,
        heroReplayErrorWorldUnits=stats(hero_delta),cameraReplayErrorWorldUnits=stats(cam_delta),heroEndpointErrorWorldUnits=math.hypot(end['heroX']-end_source['heroX'],end['heroY']-end_source['heroY']),sourceStartToEndWorldUnits=math.hypot(end_source['heroX']-start['heroX'],end_source['heroY']-start['heroY']),
        telemetryFrames=len(by_key),telemetryMissingFrames=truth[-1]['sample']['unityFrame']-truth[0]['sample']['unityFrame']+1-len(by_key),captureFrames=len(capkeys),analysisFrames=len(analysiskeys),presentedFrames=len(presentkeys),captureCoverage=len(capkeys)/len(by_key),analysisCoverage=len(analysiskeys)/len(by_key),presentationCoverage=len(presentkeys)/len(by_key),
        solverError=stats([r['error'] for r in solver]),solverEndpointError=solver[-1]['error'] if solver else None,solverVerifiedWrongOver8px=sum(r['verified'] and r['error']>8 for r in solver),solverVerifiedSamples=sum(r['verified'] for r in solver),
        displayError=stats([r['error'] for r in presented]),displayEndpointError=next((r['error'] for r in reversed(presented) if r['error'] is not None),None),
        analysisIntervalMs=stats(intervals(tracking,'offset')),displayNewFrameIntervalMs=stats(intervals(unique_display,'observedAt')),displayAgeMs=stats([r['ageMs'] for r in presented]),
        at30fps=dict(ticks=len(ticks),valid=sum(r['valid'] for r in ticks),reused=sum(r['reused'] for r in ticks),within2px=sum(r['within2px'] for r in ticks),within8px=sum(r['within8px'] for r in ticks),freshAndWithin2px=sum(r['freshAndWithin2px'] for r in ticks),ageMs=stats([r['ageMs'] for r in ticks]),imageFrameLag=stats([r['gameFrameLag'] for r in ticks]),error=stats([r['error'] for r in ticks]),errorVersusCurrentGame=stats([r['errorVersusCurrentGame'] for r in ticks])))
    output.mkdir(parents=True,exist_ok=True)
    (output/'summary.json').write_text(json.dumps(report,indent=2))
    write_csv(output/'all-game-frames.csv',every_frame);write_csv(output/'solver.csv',solver);write_csv(output/'presentation.csv',presented);write_csv(output/'30fps.csv',ticks)
    print(json.dumps(report,indent=2))
    return report

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('source',type=Path);p.add_argument('replay',type=Path);p.add_argument('audit',type=Path);p.add_argument('output',type=Path);a=p.parse_args();evaluate(a.source,a.replay,a.audit,a.output)
