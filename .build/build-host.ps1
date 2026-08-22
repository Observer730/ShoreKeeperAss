if ([string]::IsNullOrEmpty($env:ProgramData)) { $env:ProgramData = 'C:\ProgramData' }
if ([string]::IsNullOrEmpty($env:NUGET_PACKAGES)) { Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue }
& 'C:\Program Files\dotnet\dotnet.exe' build 'C:\Project\ShoreKeeperAss\ShoreKeeperHost\ShoreKeeper.Host.Wpf\ShoreKeeper.Host.Wpf\ShoreKeeper.Host.Wpf.csproj' -v q --nologo
exit $LASTEXITCODE
