import hashlib,json,tempfile,unittest
from pathlib import Path
import compact_markings as cm
import hkv_bot_dataset as ds

class CompactMarkingsTests(unittest.TestCase):
    def test_compact_schema_retires_raster_lists_and_joni(self):
        self.assertEqual(ds.SCHEMA_VERSION,14)
        self.assertEqual(len(ds.FEATURE_NAMES),205)
        self.assertEqual(len(ds.SCHEMAS[12]),1202)
        self.assertEqual(ds.ENEMY_START,16)
        self.assertFalse(any(n.startswith(('screen_','joni_','terrain_0_','wall_left_')) for n in ds.FEATURE_NAMES))
    def test_shared_slots_keep_all_types_within_shared_limit(self):
        g=dict(width=640,height=360,floors=[dict(minimumX=i*20,maximumX=i*20+10,y=100) for i in range(10)],
            walls=[dict(x=i*40,bottomY=30,topY=90) for i in range(3)],
            spikes=[dict(x1=10,y1=80,x2=40,y2=80)],doors=[dict(x1=300,y1=40,x2=300,y2=90)])
        values=cm.encode(g,.5,.3)
        self.assertEqual(values[:9],[1,1,1,1,10/16,3/16,1/16,1/16,0])
        self.assertEqual(sum(values[14::6]),15)
        g['floors'].reverse();g['walls'].reverse()
        self.assertEqual(values,cm.encode(g,.5,.3))
    def test_overflow_and_unknown_are_explicit(self):
        g=dict(width=200,height=100,floors=[dict(minimumX=i*2,maximumX=i*2+1,y=40) for i in range(70)],walls=[])
        v=cm.encode(g,.3,.25)
        self.assertEqual(v[:4],[1,1,0,0]);self.assertEqual(v[8],54/16);self.assertEqual(sum(v[14::6]),16)
        self.assertEqual(cm.encode(None,.3,.25),[0.]*105)
    def test_type_values_nearest_selection_and_previous_schema(self):
        g=dict(width=200,height=100,floors=[dict(minimumX=150,maximumX=160,y=80)],
            walls=[dict(x=160,bottomY=40,topY=80)],
            spikes=[dict(x1=99,y1=50,x2=101,y2=50)],doors=[dict(x1=180,y1=40,x2=180,y2=60)])
        v=cm.encode(g,.5,.5)
        slots=[v[i:i+6] for i in range(9,len(v),6)]
        self.assertEqual(slots[0][4:],[0.,1.])
        self.assertEqual(set(s[4] for s in slots if s[5]),{0.,.33,.66,1.})
        old=cm.encode(g,.5,.5,version=13)
        self.assertEqual(len(old),393)
        self.assertEqual(old[13],.75)
        self.assertEqual(len(ds.SCHEMAS[13]),493)
        g['floors'] += [dict(minimumX=i,maximumX=i+1,y=95) for i in range(40)]
        v=cm.encode(g,.5,.5)
        self.assertEqual(len(v),105)
        self.assertEqual(v[13:15],[0.,1.])
        self.assertEqual(sum(v[14::6]),16)
    def test_recovery_requires_frozen_labels_and_exact_frame_match(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp);clip=root/'demo-12345678-1234-1234-1234-123456789abc';clip.mkdir()
            rid='12345678-1234-1234-1234-123456789abc'
            manifest=dict(id=rid,replayID=rid,botDomain='hacker');(clip/'manifest.json').write_text(json.dumps(manifest))
            raw=json.dumps(dict(spikes=[dict(kind='spike',sceneName='room',minimumWorldX=-1,maximumWorldX=1,worldY=0)])).encode()
            (clip/'authored-ground.json').write_bytes(raw)
            context=dict(replayRoot=str(root),authoredGroundSHA256=hashlib.sha256(raw).hexdigest());(clip/'audit-context.json').write_text(json.dumps(context))
            archive=root/('frames-'+rid);archive.mkdir();(archive/'manifest.json').write_text(json.dumps(dict(pathID=rid,frames=[dict(captureTimestamp=1,captureGeneration=2,renderedGameFrame=42)])))
            sample=dict(unityFrame=42,cameraAvailable=True,projectionPixelWidth=200,projectionPixelHeight=100,pixelsPerWorldUnitX=10,pixelsPerWorldUnitY=10,cameraX=0,cameraY=0,sceneName='room')
            (root/('path-test-'+rid+'.json')).write_text(json.dumps(dict(id=rid,groundTruthSamples=[dict(receivedTimestamp=1.01,sample=sample)])))
            row=dict(captureTimestamp=1,generation=2,capturedAt=1,authoredTerrain=dict(width=200,height=100,floors=[],walls=[]))
            report=cm.recover_recorded_spikes(clip,manifest,[row]);self.assertEqual(report['matchedFrames'],1)
            self.assertEqual(row['authoredTerrain']['spikes'],[dict(x1=90,y1=50,x2=110,y2=50)])
            wrong=dict(row,generation=3,authoredTerrain=dict(width=200,height=100,floors=[],walls=[]))
            cm.recover_recorded_spikes(clip,manifest,[wrong]);self.assertNotIn('spikes',wrong['authoredTerrain'])
            (clip/'authored-ground.json').write_text('{}')
            with self.assertRaises(ValueError):cm.recover_recorded_spikes(clip,manifest,[wrong])

if __name__=='__main__':unittest.main()
