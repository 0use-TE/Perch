using Perch.Tests;

// Settings and chats are redirected to a throwaway folder before anything touches them.
var data = Path.Combine(Path.GetTempPath(), "perch-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(data);
Environment.SetEnvironmentVariable("PERCH_DATA", data);

try
{
    await MarkdownTests.Run();
    await DsmlTests.Run();
    await ClientTests.Run();
    await AgentTests.Run();
    await ChatLineTests.Run();
    await ClipboardTests.Run();
    await SearchTests.Run();
    await StoreTests.Run();
#if WINDOWS
    await WindowsTests.Run();
#endif
}
finally
{
    try { Directory.Delete(data, true); } catch (IOException) { }
}

Console.WriteLine();
Console.WriteLine($"{T.Passed} passed, {T.Failures.Count} failed");
foreach (var failure in T.Failures) Console.WriteLine("  FAILED: " + failure);
return Math.Min(T.Failures.Count, 100);
