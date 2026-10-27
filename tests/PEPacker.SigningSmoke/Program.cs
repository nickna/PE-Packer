using PEPacker;
using PEPacker.Bundling;
using PEPacker.Signing;

if (args.Length == 0)
{
    Console.WriteLine("PE-Packer signed executable smoke");
    return 0;
}
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PEPacker.SigningSmoke <certificate.pfx> <output.exe>");
    return 64;
}
var output = Path.GetFullPath(args[1]);
AppHostGenerator.CreateSingleFileExecutable(new BundleRequest
{
    EntryAssemblyPath = typeof(Program).Assembly.Location,
    OutputPath = output,
    AssemblyName = "PEPacker.SigningSmoke",
    AdditionalAssemblies = [typeof(AuthenticodeSigner).Assembly.Location],
    RuntimeIdentifier = "win-x64",
    FrameworkVersion = new Version(10, 0)
}, BundlerMode.BuiltIn);
await AuthenticodeSigner.SignAsync(output, new AuthenticodeSigningOptions
{
    Certificate = args[0],
    Password = Environment.GetEnvironmentVariable("PEPACKER_SIGNING_SMOKE_PASSWORD"),
    TimestampUrl = null
});
Console.WriteLine($"Signed {output}");
return 0;
