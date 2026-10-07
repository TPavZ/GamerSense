using GamerSense.Audio;
using NAudio.Wave;
using System.Diagnostics;

public static class LiveVolumeChecks
{
    public static void Run()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000,2);
        byte[] Tone(int frames,float value=.4f)
        {
            var data=new byte[frames*8];
            for(int i=0;i<data.Length;i+=8){BitConverter.GetBytes(value).CopyTo(data,i);BitConverter.GetBytes(-value).CopyTo(data,i+4);}
            return data;
        }
        foreach(var f in new WaveFormat[]{format,new WaveFormat(48000,16,2),new WaveFormat(48000,24,2),new WaveFormat(48000,32,2),new WaveFormatExtensible(48000,32,2)})
        {
            byte[] original=new byte[f.BlockAlign*1200];new Random(42).NextBytes(original);var actual=original.ToArray();
            new LiveCategoryVolumes(f,new VolumeControls(),()=>"explosions").Process(actual,0,actual.Length);
            if(!actual.SequenceEqual(original))throw new Exception("Neutral volume changed native bytes: "+f);
            new LiveCategoryVolumes(f,new VolumeControls{Levels=new(Enabled:true)},()=>"explosions").Process(actual,0,actual.Length);
            if(!actual.SequenceEqual(original))throw new Exception("Enabled unity routing changed native bytes: "+f);
        }
        foreach(string category in new[]{"gunfire","explosions","footsteps","ground_vehicles","air_vehicles"})
        {
            var controls=new VolumeControls { Levels=new(Overall:50,Explosions:50,Footsteps:50,GroundVehicles:50,AirVehicles:50,Enabled:true,Gunfire:50) };
            var gain=new LiveCategoryVolumes(format,controls,()=>category);var data=Tone(24000);gain.Process(data,0,data.Length);
            if(Math.Abs(BitConverter.ToSingle(data,data.Length-8)-.1f)>1e-6 || Math.Abs(BitConverter.ToSingle(data,data.Length-4)+.1f)>1e-6)
                throw new Exception("Category/overall gain or stereo preservation failed.");
            float first=BitConverter.ToSingle(data,0);
            if(first<.39f||first>.4f)throw new Exception("Gain jumped without ramp.");
        }
        foreach(int bits in new[]{16,24,32})
        {
            var pcm=new WaveFormat(48000,bits,2);var data=new byte[pcm.BlockAlign*2400];int bytes=bits/8;
            for(int i=0;i<data.Length;i+=pcm.BlockAlign)
            {
                long positive=1L<<(bits-2),negative=-positive;
                for(int b=0;b<bytes;b++){data[i+b]=(byte)(positive>>(8*b));data[i+bytes+b]=(byte)(negative>>(8*b));}
            }
            new LiveCategoryVolumes(pcm,new(){Levels=new(Overall:50)},()=>null).Process(data,0,data.Length);
            int last=data.Length-pcm.BlockAlign;
            long Sample(int i)=>bits switch{16=>BitConverter.ToInt16(data,i),24=>((data[i]|data[i+1]<<8|data[i+2]<<16)<<8>>8),_=>BitConverter.ToInt32(data,i)};
            if(Sample(last)!=(1L<<(bits-3))||Sample(last+bytes)!=-(1L<<(bits-3)))throw new Exception("PCM gain quantization/stereo failed: "+bits);
        }
        var muteControls=new VolumeControls { Levels=new(Explosions:0,Enabled:true) };
        string? selected="explosions";var muteGain=new LiveCategoryVolumes(format,muteControls,()=>selected);
        var muted=Tone(24000);muteGain.Process(muted,0,muted.Length);
        if(BitConverter.ToSingle(muted,muted.Length-8)!=0)throw new Exception("Category mute failed.");
        selected=null;var recovered=Tone(24000);muteGain.Process(recovered,0,recovered.Length);
        if(BitConverter.ToSingle(recovered,recovered.Length-8)!=.4f)throw new Exception("Uncertain fallback did not return to normal.");
        muteControls.Levels=muteControls.Levels with {Enabled=false};selected="explosions";
        var bypass=Tone(2400);var untouched=bypass.ToArray();muteGain.Process(bypass,0,bypass.Length);
        if(!bypass.SequenceEqual(untouched))throw new Exception("Disabled category routing still applied category gain.");
        var stable=new StableCategoryRoute();
        if(stable.Update("ground_vehicles",false,0)!=null||stable.Update("ground_vehicles",false,125)!="ground_vehicles")throw new Exception("Route stabilization onset failed.");
        if(stable.Update(null,false,250)!="ground_vehicles"||stable.Update("ground_vehicles",false,375)!="ground_vehicles")throw new Exception("One ambiguous estimate chopped the stable route.");
        if(stable.Update(null,false,650)!=null)throw new Exception("Ambiguous hold was extended indefinitely.");
        stable.Update("ground_vehicles",false,750);stable.Update("ground_vehicles",false,875);
        if(stable.Update(null,true,900)!=null)throw new Exception("Clear other audio kept stale attenuation.");
        stable.Update("explosions",false,1000);stable.Update("explosions",false,1125);stable.Reset();
        if(stable.Category!=null)throw new Exception("Gap reset kept stabilized category.");
        var smoothControls=new VolumeControls{Levels=new(Explosions:25,Enabled:true)};
        selected="explosions";var smooth=new LiveCategoryVolumes(format,smoothControls,()=>selected);
        var startFade=Tone(480);smooth.Process(startFade,0,startFade.Length);
        if(BitConverter.ToSingle(startFade,startFade.Length-8)<.37f)throw new Exception("Detection still caused a fast audible gain drop.");
        var settle=Tone(24000);smooth.Process(settle,0,settle.Length);selected=null;
        var releaseFade=Tone(480);smooth.Process(releaseFade,0,releaseFade.Length);
        if(BitConverter.ToSingle(releaseFade,releaseFade.Length-8)>.12f)throw new Exception("Category release jumped back to full mix.");
        smoothControls.Levels=smoothControls.Levels with{Enabled=false};var off=Tone(4800);smooth.Process(off,0,off.Length);
        if(BitConverter.ToSingle(off,off.Length-8)!=.4f)throw new Exception("Routing-off bypass did not recover promptly.");
        var loud=new VolumeControls {Levels=new(Overall:150)};var boosted=Tone(2400,.9f);var limiter=new LiveCategoryVolumes(format,loud,()=>null);
        limiter.Process(boosted,0,boosted.Length);
        if(BitConverter.ToSingle(boosted,boosted.Length-8)>1 || limiter.ClippedSamples==0)throw new Exception("Boost clipping guard/count failed.");
        loud.Levels=new(Overall:double.NaN,Footsteps:-1,Explosions:999);
        if(loud.Levels.Overall!=100||loud.Levels.Footsteps!=0||loud.Levels.Explosions!=150)throw new Exception("Unsafe persisted volume values accepted.");
        var legacy=System.Text.Json.JsonSerializer.Deserialize<VolumeLevels>("{\"Explosions\":70,\"Footsteps\":110}")!;
        if(legacy.Gunfire!=100||legacy.Explosions!=70||legacy.Footsteps!=110)throw new Exception("Legacy volumes did not retain neutral gunfire default.");
        var saved=System.Text.Json.JsonSerializer.Deserialize<VolumeLevels>(System.Text.Json.JsonSerializer.Serialize(new VolumeLevels(Gunfire:35)))!;
        if(saved.Gunfire!=35)throw new Exception("Gunfire setting round-trip failed.");
        loud.Levels=new(Gunfire:double.PositiveInfinity);if(loud.Levels.Gunfire!=100)throw new Exception("Unsafe gunfire volume accepted.");
        var source=new BufferedWaveProvider(format){ReadFully=false};var raw=Tone(2400);var rawCopy=raw.ToArray();source.AddSamples(raw,0,raw.Length);
        var meter=new MeteredPlaybackProvider(source,new LiveCategoryVolumes(format,new(){Levels=new(Overall:50)},()=>null).Process);
        var rendered=new byte[raw.Length+8];meter.Read(rendered,8,raw.Length);
        if(!raw.SequenceEqual(rawCopy)||meter.AvailableFrames!=0||meter.ShortReadCount!=0||Math.Abs(BitConverter.ToSingle(rendered,rendered.Length-8)-.2f)>1e-6)
            throw new Exception("Output gain changed capture data or added buffering/supply errors.");
        string root=Path.Combine(AppContext.BaseDirectory,"route-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string model=Path.Combine(root,"limits.json");File.WriteAllText(model,"{\"liveRoutingMaxDistance\":{\"explosions\":1,\"gunfire\":1}}");
        var settings=new VolumeControls {Levels=new(Enabled:true)};
        EventSuggestion Match(double first,double second)=>new(){SuggestedLabel="explosions / mortars",WindowsAnalyzed=1,Alternatives=[new("explosions","explosions / mortars",first),new("gunfire","gunfire",second)]};
        var result=Match(.1,.5);
        using(var router=new LiveCategoryRouter(format,settings,model,_=>Volatile.Read(ref result)))
        {
            bool FeedUntil(Func<bool> done,int ms=2500)
            {
                var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<ms){router.Tap(raw,raw.Length);Thread.Sleep(30);if(done())return true;}return false;
            }
            if(!FeedUntil(()=>router.CurrentCategory=="explosions"))throw new Exception("Stable supported live category did not route.");
            result=new(){SuggestedLabel="gunfire",WindowsAnalyzed=1,Alternatives=[new("gunfire","gunfire",.1),new("explosions","explosions / mortars",.5)]};
            router.Reset();if(!FeedUntil(()=>router.CurrentCategory=="gunfire"))throw new Exception("Gunfire did not route as a supported category.");
            Thread.Sleep(350);if(router.CurrentCategory!=null)throw new Exception("Stale category estimate kept applying gain.");
            router.Reset();if(router.CurrentCategory!=null)throw new Exception("Analysis gap did not clear routing.");
            result=Match(.1,.11);router.Reset();FeedUntil(()=>false,650);
            if(router.CurrentCategory!=null)throw new Exception("Close alternative was routed as certain.");
            result=Match(10,20);router.Reset();FeedUntil(()=>false,650);
            if(router.CurrentCategory!=null)throw new Exception("Out-of-profile audio was routed.");
            settings.Levels=settings.Levels with{Enabled=false};router.Tap(raw,raw.Length);
            if(router.CurrentCategory!=null)throw new Exception("Disable retained route.");
        }
        Console.WriteLine("PASS live volume: native unity bypass, stereo gain, smooth ramps, category/overall/mute, unknown/off/stale/gap fallback, bounds/clipping and no added output queue");
    }
}
