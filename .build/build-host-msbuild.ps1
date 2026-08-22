$env:MSBuildSDKsPath = 'C:\Program Files\dotnet\sdk\9.0.309\Sdks'
$env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
& 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' 'C:\Project\ShoreKeeperAss\ShoreKeeperHost\ShoreKeeper.Host.Wpf\ShoreKeeper.Host.Wpf\ShoreKeeper.Host.Wpf.csproj' /restore /v:m /nologo
exit $LASTEXITCODE
