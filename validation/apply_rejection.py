import pathlib,json,csv,collections,numpy as np
out=pathlib.Path('outputs/Wardogs-v0.3.1-model')
rows=list(csv.DictReader((out/'held-out-predictions.csv').open()))
model=json.loads((out/'prototype-model.json').read_text())
model['version']='0.3-conservative-experimental'
model['rejectionVersion']='margin-distance-v1'
model['rejection']={c:dict(minMargin=.60 if c=='reload' else .25,maxDistance=.75 if c=='reload' else 1.5) for c in model['centers']}
model['limitations']=['Heuristic rejection, not calibrated confidence.','Broad user-reviewed intervals are weak labels.','Own/nearby movement combined.','No audio filtering.','Normal-volume unknown or overlapping sounds may still be mislabeled.']
(out/'prototype-model.json').write_text(json.dumps(model,indent=2))
def accept(row):
    gate=model['rejection'][row['prediction']]
    return float(row['margin'])>=gate['minMargin'] and float(row['distance'])<=gate['maxDistance']
for r in rows:r['conservativePrediction']=r['prediction'] if accept(r) else 'uncertain'
report={'purpose':'Pilot heuristic to reduce forced guesses; thresholds are not calibrated probabilities.','method':'Apply fixed distance and runner-up margin gates to previous leave-one-recording-out predictions. Same recordings used for model development; this is a retrospective comparison, not independent validation.','gates':model['rejection'],'windows':len(rows),'uncertainWindows':sum(r['conservativePrediction']=='uncertain' for r in rows),'classes':{}}
for c in model['centers']:
    before_fp=sum(r['prediction']==c and r['label']!=c for r in rows)
    after_fp=sum(r['conservativePrediction']==c and r['label']!=c for r in rows)
    before_tp=sum(r['prediction']==c and r['label']==c for r in rows)
    after_tp=sum(r['conservativePrediction']==c and r['label']==c for r in rows)
    report['classes'][c]=dict(falsePositiveWindowsBefore=before_fp,falsePositiveWindowsAfter=after_fp,truePositiveWindowsBefore=before_tp,truePositiveWindowsAfter=after_tp)
(out/'rejection-comparison.json').write_text(json.dumps(report,indent=2))
with (out/'conservative-predictions.csv').open('w',newline='') as f:
    w=csv.DictWriter(f,fieldnames=list(rows[0]));w.writeheader();w.writerows(rows)
print(json.dumps(report,indent=2))
