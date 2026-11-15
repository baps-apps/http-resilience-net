// Runs SmokeScenarios in a Native AOT binary. The integration tests run the same scenarios under JIT on every
// `dotnet test`, and publish and run this binary when HTTPRESILIENCE_AOT_SMOKE=1 (NativeAotSmokeTests).

IReadOnlyList<string> failures = await SmokeScenarios.RunAsync();

if (failures.Count > 0)
{
    Console.Error.WriteLine($"AOT smoke FAILED:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", failures)}");
    return 1;
}

Console.WriteLine("AOT smoke passed: configuration bound, the pipeline ran and the host started under Native AOT.");
return 0;
