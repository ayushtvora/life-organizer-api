# Runs the test suite in a .NET 10 SDK container.
# Use this on Windows machines where Smart App Control blocks loading locally built DLLs.
# The source is copied inside the container so Linux obj/ files never touch the Windows checkout.
param([string]$Filter = "")

$repo = (Resolve-Path "$PSScriptRoot\..").Path
$filterArg = if ($Filter) { "--filter '$Filter'" } else { "" }

docker run --rm -v "${repo}:/repo:ro" mcr.microsoft.com/dotnet/sdk:10.0 sh -c @"
mkdir /work && cd /repo && tar --exclude=bin --exclude=obj --exclude=.git -cf - . | tar -xf - -C /work && cd /work && dotnet test $filterArg
"@
exit $LASTEXITCODE
