using NAudio.Wave;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GamerSense.Audio;

public sealed record LearningReport(string ModelFile, string ModelHash, bool Passed, int AcceptedClips,
    int SkippedClips, int DuplicateClips, int ConflictingClips, int TrainingClips, int ValidationClips,
    string[] UpdatedCategories, string[] WaitingCategories, double CurrentAccuracy, double CandidateAccuracy,
    string Summary);

// Human-reviewed prototypes supplement the bundled profiles. Nothing trains on
// predictions, playback-processed audio, or held-out sessions.
public static class ApprovedProfileTrainer
{
    private sealed record Example(string Hash, string Category, string Group, double[] Feature);
    private static string? Category(string label) => label.Trim().ToLowerInvariant() switch
    {
        "footsteps" => "footsteps", "movement" or "body_movement" => "body_movement",
        "gunfire" => "gunfire", "air vehicle" or "air_vehicles" => "air_vehicles",
        "ground vehicle" or "ground_vehicles" => "ground_vehicles",
        "explosions / mortars" or "explosions" => "explosions",
        "ambience" or "other_ambience" => "other_ambience", "horn / alarm" or "horn_alarm" => "horn_alarm",
        "building / repair" or "building_repair" => "building_repair", "speech" or "voice" => "voice",
        "reload" => "reload", "chambering" => "chambering", "detonator activation" => "detonator_activation",
        "grenade handling / throw" => "grenade_handling_throw", _ => null
    };
    public static string HashFile(string path)
    { using var stream=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public static LearningReport Train(string folder, string bundled, string current, string destination)
    {
        if(!Directory.Exists(folder))throw new DirectoryNotFoundException("The approved folder does not exist yet. Approve some clips first.");
        var model=JsonNode.Parse(File.ReadAllText(bundled))!.AsObject();
        if(!new EventCategorizer(bundled).Available||!new EventCategorizer(current).Available)throw new InvalidDataException("Classification profiles are unavailable.");
        var means=model["mean"]!.Deserialize<double[]>()!;var scales=model["scale"]!.Deserialize<double[]>()!;
        var examples=new List<Example>();int skipped=0,duplicates=0,conflicts=0;
        // Bound each run; the UI reports skipped inputs, never rewrites them.
        foreach(var json in Directory.EnumerateFiles(folder,"*.json",SearchOption.AllDirectories).OrderBy(x=>x,StringComparer.Ordinal).Take(5001))
        {
            if(examples.Count>=5000){skipped++;continue;}
            try
            {
                var info=new FileInfo(json);if(info.Length>1024*1024)throw new InvalidDataException();
                using var doc=JsonDocument.Parse(File.ReadAllText(json));var label=doc.RootElement;
                if(!label.TryGetProperty("verified",out var verified)||verified.ValueKind!=JsonValueKind.True ||
                    !label.TryGetProperty("labelSource",out var source)||source.GetString()!="manual event review" ||
                    !label.TryGetProperty("sampleUse",out var use)||use.GetString()!="useful training sample")throw new InvalidDataException();
                string? category=Category(label.GetProperty("label").GetString()??"");
                if(category is null||model["prototypes"]![category] is null)throw new InvalidDataException();
                string wav=Path.ChangeExtension(json,".wav");
                if(label.GetProperty("audio").GetString()!=Path.GetFileName(wav)||new FileInfo(wav).Length>64*1024*1024)throw new InvalidDataException();
                using var reader=new WaveFileReader(wav);
                if(reader.WaveFormat.SampleRate!=48000||reader.WaveFormat.Channels!=2||reader.TotalTime.TotalSeconds<.1||reader.TotalTime.TotalSeconds>30)throw new InvalidDataException();
                // Hash native sample payload, so duplicate exports with different WAV headers count once.
                var native=new byte[checked((int)reader.Length)];int offset=0,n;
                while(offset<native.Length&&(n=reader.Read(native,offset,native.Length-offset))>0)offset+=n;
                if(offset!=native.Length)throw new InvalidDataException();
                string hash=Convert.ToHexString(SHA256.HashData(native)).ToLowerInvariant();reader.Position=0;
                var sample=reader.ToSampleProvider();var windows=new List<double[]>();
                int frames=(int)(reader.Length/reader.WaveFormat.BlockAlign);
                // Up to 12 evenly spaced half-second windows; each clip gets one vote.
                int count=Math.Min(12,Math.Max(1,(frames+23999)/24000));
                for(int i=0;i<count;i++)
                {
                    int start=count==1?0:(int)((long)i*Math.Max(0,frames-24000)/(count-1));
                    reader.Position=(long)start*reader.WaveFormat.BlockAlign;
                    var stereo=new float[48000];int read=0;
                    while(read<stereo.Length&&(n=sample.Read(stereo,read,stereo.Length-read))>0)read+=n;
                    if(stereo.Any(x=>!float.IsFinite(x)))throw new InvalidDataException();
                    if(read==0||Math.Sqrt(stereo.Sum(x=>(double)x*x)/read)<Math.Pow(10,-65/20.0))continue;
                    windows.Add(WardogsModel.ExtractFeatures(stereo));
                }
                if(windows.Count==0)throw new InvalidDataException();
                var feature=Enumerable.Range(0,47).Select(i=>(windows.Average(w=>w[i])-means[i])/scales[i]).ToArray();
                string session=label.TryGetProperty("sessionId",out var id)?id.GetString()??"":"";
                // Missing provenance cannot establish independent recording sessions.
                if(label.TryGetProperty("sourceAudioFile",out var imported) && imported.ValueKind==JsonValueKind.String && !string.IsNullOrWhiteSpace(imported.GetString()))
                    session="import:"+imported.GetString()!.ToLowerInvariant();
                if(string.IsNullOrWhiteSpace(session))throw new InvalidDataException();
                examples.Add(new(hash,category,session,feature));
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException or NotSupportedException){skipped++;}
        }
        var unique=new List<Example>();
        foreach(var group in examples.GroupBy(x=>x.Hash))
        {
            if(group.Select(x=>x.Category).Distinct().Count()>1){conflicts+=group.Count();continue;}
            unique.Add(group.First());duplicates+=group.Count()-1;
        }
        var train=new List<Example>();var validation=new List<Example>();var updated=new List<string>();var waiting=new List<string>();
        // Hold out whole sessions globally, so cross-category windows from a held-out
        // recording never become training examples in another category.
        var groups=unique.Select(x=>x.Group).Distinct().OrderBy(x=>HashText(x)).ToArray();
        var held=groups.Where(g=>Convert.ToUInt32(HashText(g)[..8],16)%3==0).ToHashSet();
        foreach(var category in unique.GroupBy(x=>x.Category))
        {
            var training=category.Where(x=>!held.Contains(x.Group)).ToArray();var testing=category.Where(x=>held.Contains(x.Group)).ToArray();
            if(category.Count()<5||category.Select(x=>x.Group).Distinct().Count()<3||training.Length<3||testing.Length==0)
            {waiting.Add(category.Key);continue;}
            updated.Add(category.Key);train.AddRange(training);validation.AddRange(testing);
            var prototypes=model["prototypes"]![category.Key]!.AsArray();
            // Bounded deterministic representatives, not thousands of near-duplicate windows.
            foreach(var e in training.OrderBy(x=>x.Hash).Where((_,i)=>i%Math.Max(1,(training.Length+15)/16)==0).Take(16))
                prototypes.Add(JsonSerializer.SerializeToNode(e.Feature));
        }
        double Score(JsonObject profiles,Example e)
        {
            return profiles.Select(p=>(Category:p.Key,Distance:p.Value!.Deserialize<double[][]>()!.Min(v=>v.Select((x,i)=>Math.Pow(x-e.Feature[i],2)).Average())))
                .OrderBy(p=>p.Distance).First().Category==e.Category?1:0;
        }
        var existing=JsonNode.Parse(File.ReadAllText(current))!["prototypes"]!.AsObject();
        var candidate=model["prototypes"]!.AsObject();
        double before=validation.Count==0?0:validation.GroupBy(x=>x.Category).Average(g=>g.Average(e=>Score(existing,e)));
        double after=validation.Count==0?0:validation.GroupBy(x=>x.Category).Average(g=>g.Average(e=>Score(candidate,e)));
        bool passed=updated.Count>0&&validation.GroupBy(x=>x.Category).All(g=>g.Average(e=>Score(candidate,e))>=.5 && g.Average(e=>Score(candidate,e))>=g.Average(e=>Score(existing,e)))&&after>=before;
        Directory.CreateDirectory(destination);string dir=Path.Combine(destination,"candidate-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string modelFile=Path.Combine(dir,"profiles.json");
        model["personalLearning"]=JsonSerializer.SerializeToNode(new { createdUtc=DateTime.UtcNow,trainingClips=train.Count,validationClips=validation.Count,split="stable hash split; whole recording sessions / imported source files held out globally", bundledTrainingOverlapUnknown=true,trainingHashes=train.Select(x=>x.Hash),validationHashes=validation.Select(x=>x.Hash),trainingSessions=train.Select(x=>HashText(x.Group)).Distinct(),validationSessions=validation.Select(x=>HashText(x.Group)).Distinct(),updatedCategories=updated,notCalibratedConfidence=true });
        File.WriteAllText(modelFile,model.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        string summary=$"{unique.Count} unique approved clips; {train.Count} training / {validation.Count} held out. "+
            $"Current / candidate check score: {before:P0} / {after:P0}. "+
            (passed?"Candidate passed the available session check. Apply while playback is stopped.":"Candidate not ready: more independent approved sessions or better validation results needed.")+
            $"\nSkipped: {skipped}; duplicates: {duplicates}; conflicting labels excluded: {conflicts}."+
            "\nSessions are held out from this update; some may already be in bundled training. Scores are a regression check, not proof of accuracy on unseen games or rejection of unknown sounds.";
        var report=new LearningReport(modelFile,HashFile(modelFile),passed,unique.Count,skipped,duplicates,conflicts,train.Count,validation.Count,updated.ToArray(),waiting.ToArray(),before,after,summary);
        File.WriteAllText(Path.Combine(dir,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));return report;
    }
    private static string HashText(string text)=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}

public sealed class PersonalProfiles
{
    public string BundledPath {get;}
    private readonly string _directory;
    private EventCategorizer _categorizer;
    public string ActivePath {get;private set;}
    public string Status => ActivePath==BundledPath?"Bundled profiles active":"Personal profiles active";
    public PersonalProfiles(string? bundled=null,string? directory=null)
    {
        BundledPath=bundled??Path.Combine(AppContext.BaseDirectory,"Models","spike-profiles.json");
        _directory=directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GamerSense","Learning");
        string active=Path.Combine(_directory,"active.json");
        ActivePath=File.Exists(active)&&new EventCategorizer(active).Available?active:BundledPath;
        _categorizer=new(ActivePath);
    }
    public EventSuggestion Categorize(EventClip clip)=>Volatile.Read(ref _categorizer).Categorize(clip);
    public LearningReport Train(string folder)=>ApprovedProfileTrainer.Train(folder,BundledPath,ActivePath,_directory);
    public void Apply(LearningReport report)
    {
        if(!report.Passed||ApprovedProfileTrainer.HashFile(report.ModelFile)!=report.ModelHash||!new EventCategorizer(report.ModelFile).Available)
            throw new InvalidDataException("The candidate is not validated or has changed. Retrain before applying.");
        Directory.CreateDirectory(_directory);string active=Path.Combine(_directory,"active.json"),temp=active+".tmp";
        File.Copy(report.ModelFile,temp,true);File.Move(temp,active,true);
        ActivePath=active;Volatile.Write(ref _categorizer,new(active));
    }
    public void RestoreBundled()
    {
        string active=Path.Combine(_directory,"active.json");if(File.Exists(active))File.Delete(active);
        ActivePath=BundledPath;Volatile.Write(ref _categorizer,new(BundledPath));
    }
}
