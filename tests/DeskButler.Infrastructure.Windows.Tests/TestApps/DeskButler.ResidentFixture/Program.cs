const string markerFileName = "DeskButler.ResidentFixture.started";

if (args is ["--wait"])
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

if (args.Length != 0)
{
    Environment.ExitCode = 2;
    return;
}

File.WriteAllText(Path.Combine(AppContext.BaseDirectory, markerFileName), "started");
