import json, pathlib, wave, collections, csv, sys
import numpy as np
BASE=pathlib.Path(sys.argv[1]) if len(sys.argv)>1 else pathlib.Path('Wardogs-samples')
OUT=pathlib.Path(sys.argv[2]) if len(sys.argv)>2 else pathlib.Path('Wardogs-model-results'); OUT.mkdir(parents=True,exist_ok=True)
labels=json.loads((BASE/'wardogs-reviewed-labels.json').read_text())
MAP={'nearby footsteps':'movement','own footsteps':'movement','jump':'movement','crawl movement':'movement','gunfire':'gunfire','reload':'reload','explosion (source unknown)':'explosion','ground vehicle moving':'ground vehicle','air vehicle idle':'air vehicle','air vehicle approach':'air vehicle','air vehicle departure':'air vehicle','air vehicle landing':'air vehicle','ambience':'ambience','horn/alarm':'horn/alarm','chambering':'chambering'}
# Recordings are the independent groups. Unlabeled windows are never negatives.
window=.5; hop=.25
edges=np.geomspace(50,8000,21); freq=np.fft.rfftfreq(512,1/16000)
feature_names=[f'band_{i}_{stat}' for stat in ('mean','std') for i in range(20)]+['level_mean','level_std','level_p10','level_p90','zcr_mean','zcr_std','crest_mean']
def features(audio):
    # Box-average three samples per channel: identical to the live C# feature extractor.
    a=audio.reshape(-1,3,2).mean(axis=1)
    frames=np.lib.stride_tricks.sliding_window_view(a,512,axis=0)[::160]
    power=np.abs(np.fft.rfft(frames*np.hanning(512),axis=-1))**2
    power=power.mean(axis=1)
    bands=np.stack([power[:,(freq>=edges[i])&(freq<edges[i+1])].sum(axis=1) for i in range(20)],axis=1)
    bands=np.log10(bands+1e-10)
    bands-=bands.mean(axis=1,keepdims=True)
    rms=np.sqrt((frames**2).mean(axis=(1,2))+1e-12)
    level=20*np.log10(rms)
    zcr=(np.diff(np.signbit(frames),axis=-1)!=0).mean(axis=(1,2))
    crest=np.max(np.abs(frames),axis=(1,2))/(rms+1e-8)
    return np.r_[bands.mean(axis=0),bands.std(axis=0),level.mean(),level.std(),np.percentile(level,10),np.percentile(level,90),zcr.mean(),zcr.std(),crest.mean()]
def coverage(intervals,a,b):
    parts=sorted((max(a,s),min(b,e)) for s,e in intervals if s<b and e>a)
    total=0;end=a
    for s,e in parts:
        total+=max(0,e-max(s,end));end=max(end,e)
    return total/(b-a)
rows=[]; skipped=collections.Counter()
for sample in labels['samples']:
    groups=collections.defaultdict(list)
    for event in sample['events']:
        if event.get('verified') and event['end'] is not None:
            groups[MAP.get(event['label'],'uncertain')].append((event['start'],event['end']))
    with wave.open(str(BASE/sample['audio']),'rb') as w:
        assert w.getframerate()==48000 and w.getnchannels()==2 and w.getsampwidth()==2
        audio=np.frombuffer(w.readframes(w.getnframes()),dtype='<i2').reshape(-1,2)/32768.
    for start in np.arange(0,len(audio)/48000-window+1e-6,hop):
        end=start+window
        active=[k for k,v in groups.items() if coverage(v,start,end)>.05]
        eligible=[k for k in active if coverage(groups[k],start,end)>=.95]
        if len(active)!=1 or len(eligible)!=1 or eligible[0]=='uncertain':
            skipped['unlabeled_partial_or_overlapping']+=1;continue
        data=audio[round(start*48000):round(end*48000)]
        rows.append(dict(clip=pathlib.Path(sample['audio']).stem,start=float(start),end=float(end),label=eligible[0],features=features(data)))
X=np.stack([r['features'] for r in rows]); y=np.array([r['label'] for r in rows]); clips=np.array([r['clip'] for r in rows])
classes=sorted(set(y)); groups={c:sorted(set(clips[y==c])) for c in classes}
# Require at least two independent recordings for any evaluated category.
evaluated=[c for c in classes if len(groups[c])>=2]
def fit(mask):
    train=X[mask]; mean=train.mean(axis=0);scale=train.std(axis=0);scale[scale<1e-6]=1
    centers={}
    for c in evaluated:
        clipmeans=[]
        for clip in sorted(set(clips[mask & (y==c)])):
            clipmeans.append(((X[mask & (y==c)&(clips==clip)]-mean)/scale).mean(axis=0))
        if clipmeans:centers[c]=np.mean(clipmeans,axis=0)
    return mean,scale,centers
predictions=[]
for clip in sorted(set(clips)):
    train=(clips!=clip)&np.isin(y,evaluated)
    mean,scale,centers=fit(train)
    for i in np.where((clips==clip)&np.isin(y,evaluated))[0]:
        distances={c:float(np.mean(((X[i]-mean)/scale-center)**2)) for c,center in centers.items()}
        ranked=sorted(distances,key=distances.get)
        prediction=ranked[0]
        predictions.append(dict(clip=clip,start=rows[i]['start'],end=rows[i]['end'],label=y[i],prediction=prediction,distance=distances[prediction]))
metrics={}
for c in evaluated:
    tp=sum(r['label']==c and r['prediction']==c for r in predictions)
    fp=sum(r['label']!=c and r['prediction']==c for r in predictions)
    fn=sum(r['label']==c and r['prediction']!=c for r in predictions)
    precision=tp/(tp+fp) if tp+fp else 0;recall=tp/(tp+fn) if tp+fn else 0
    metrics[c]=dict(recordings=len(groups[c]),windows=int(sum(y==c)),precision=round(precision,3),recall=round(recall,3),f1=round(2*precision*recall/(precision+recall),3) if precision+recall else 0)
mean,scale,centers=fit(np.isin(y,evaluated))
model=dict(featureVersion='box3-spectrum-v1',version='0.2-experimental',game='WARDOGS',method='recording-balanced nearest centroid',sampleRate=48000,channels=2,windowSeconds=window,hopSeconds=hop,featureNames=feature_names,mean=mean.tolist(),scale=scale.tolist(),centers={c:v.tolist() for c,v in centers.items()},abstentionThreshold=None,limitations=['Forced-choice baseline; confidence and unknown rejection are not calibrated.','Broad user-reviewed intervals are weak labels, not precise individual sound onsets.','Own and nearby movement combined; no enemy identification.','No audio filtering.'])
(OUT/'prototype-model.json').write_text(json.dumps(model,indent=2))
with (OUT/'held-out-predictions.csv').open('w',newline='') as f:
    writer=csv.DictWriter(f,fieldnames=list(predictions[0]));writer.writeheader();writer.writerows(predictions)
with (OUT/'feature-windows.csv').open('w',newline='') as f:
    writer=csv.writer(f);writer.writerow(['clip','start','end','label']+feature_names)
    for r in rows:writer.writerow([r['clip'],r['start'],r['end'],r['label']]+r['features'].tolist())
report=dict(evaluation='leave-one-recording-out; preprocessing fitted within each fold',metrics=metrics,macroF1=round(np.mean([v['f1'] for v in metrics.values()]),3),excludedCategories={c:len(groups[c]) for c in classes if c not in evaluated},eligibleWindows=len(rows),excludedWindows=dict(skipped),confusion={c:dict(collections.Counter(r['prediction'] for r in predictions if r['label']==c)) for c in evaluated})
(OUT/'evaluation.json').write_text(json.dumps(report,indent=2))
lines=['# Wardogs offline detector — first baseline','', 'This is an experimental analysis model. It is not integrated into GamerSense playback.','', '## Held-out recording results','', '| Category | Recordings | Precision | Recall | F1 |','|---|---:|---:|---:|---:|']
for c,m in metrics.items():lines.append(f'| {c} | {m["recordings"]} | {m["precision"]:.1%} | {m["recall"]:.1%} | {m["f1"]:.1%} |')
lines+=['',f'Macro F1: {report["macroF1"]:.1%}. This measures forced-choice classification on selected labeled windows only, not live gameplay accuracy.','', '## What was evaluated','', 'Each original clip was held out in turn. Adjacent windows from that clip were excluded from its training fold. Only windows fully covered by one reviewed category were included. Unlabeled audio was excluded, not treated as negative. Overlapping categories were excluded. Movement actions were grouped; air vehicle actions were grouped. Each recording contributes equally to its class prototype.','', 'These recordings may share a session or environment, so holding out clips is weaker than holding out entire sessions. A single interval can contain other sounds or pauses; results depend on the interval labels.','', '## Next data priorities','', '- Ground vehicles: more idle/driving clips from different distances and sessions.','- Movement: more own/nearby movement across surfaces, distances, and backgrounds.','- Verified quiet/background intervals and mixed combat: needed to measure false alarms and calibrate unknown rejection.','- More independently recorded examples of any category with only one clip.','', 'No category has been approved for production recognition. This baseline always picks a known category and has no calibrated confidence. It cannot separate or suppress sounds.','', '## Files','', '- prototype-model.json: feature normalization and experimental class prototypes.','- held-out-predictions.csv: every evaluated window and prediction.','- feature-windows.csv: extracted feature table.','- evaluation.json: metrics and confusion counts.','- reviewed-labels.json: corrected user labels used for this run.','- train_baseline.py: reproducible preparation/evaluation source; requires Python and NumPy, run from the original workspace with outputs/Wardogs-samples available.','']
(OUT/'REPORT.md').write_text('\n'.join(lines))
print(json.dumps(report,indent=2))


