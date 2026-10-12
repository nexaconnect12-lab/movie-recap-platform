// Subprocess fault fixture only. Never used by the production import executor.
if (args.Contains("-version")) { Console.WriteLine("ffmpeg version fault-fixture"); return; }
var inputIndex = Array.IndexOf(args, "-i");
var path = inputIndex >= 0 ? args[inputIndex + 1] : args[^1];
if (path.EndsWith("flood.py", StringComparison.Ordinal))
{
    Console.Write(new string('x', 200_000)); await Task.Delay(TimeSpan.FromSeconds(60)); return;
}
if (path.EndsWith("malformed.py", StringComparison.Ordinal))
{
    Console.WriteLine("not JSON"); return;
}
if (path.EndsWith("hang.py", StringComparison.Ordinal))
{
    await File.WriteAllTextAsync(path + ".pid", Environment.ProcessId.ToString()); await Task.Delay(TimeSpan.FromSeconds(60)); return;
}
if (path.EndsWith("flood.mp4", StringComparison.Ordinal))
{
    Console.Write(new string('x', 200_000));
    await Task.Delay(TimeSpan.FromSeconds(60));
}
else if (path.EndsWith("hang.mp4", StringComparison.Ordinal))
{
    await File.WriteAllTextAsync(path + ".pid", Environment.ProcessId.ToString());
    await Task.Delay(TimeSpan.FromSeconds(60));
}
else { Console.Error.WriteLine("intentional corrupt-media fixture failure"); Environment.ExitCode = 1; }
