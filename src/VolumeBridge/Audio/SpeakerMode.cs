namespace VolumeBridge.Audio;

/// <summary>Console mode (--speaker): runs the engine and prints a status line every second.</summary>
public sealed class SpeakerMode(Engine engine, Action<string> log)
{
    public int Run(CancellationToken ct)
    {
        if (!engine.Start()) return 5;
        try
        {
            while (!ct.WaitHandle.WaitOne(1000)) log(Engine.StatusLine(engine.GetStatus()));
        }
        finally { engine.Stop(); }
        return 0;
    }
}
