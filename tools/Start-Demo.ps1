param([switch]$Build)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $repo '.local/HelloWorld'
$stateFile = Join-Path $runtime 'processes.json'

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $Arguments" }
}

if (Test-Path $stateFile) {
    foreach ($entry in (Get-Content $stateFile -Raw | ConvertFrom-Json)) {
        $running = Get-Process -Id $entry.Id -ErrorAction SilentlyContinue
        if ($running -and $running.Path -eq $entry.Path) {
            throw 'Demo is already running. Use tools/Stop-Demo.ps1 before starting again.'
        }
    }
}

foreach ($port in @(5000, 10000)) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) {
        throw "Port $port is in use. Stop the existing application first."
    }
}

if ($Build -or !(Test-Path (Join-Path $runtime 'ScadaWeb/ScadaWeb.exe'))) {
    Push-Location $repo
    try {
        $projects = @(
            'ScadaCommon/ScadaCommon.sln',
            'ScadaServer/ScadaServer/ScadaServer.sln',
            'ScadaComm/ScadaComm/ScadaComm.sln',
            'ScadaWeb/ScadaWeb/ScadaWeb.sln',
            'ScadaReport/ScadaReport.sln',
            'ScadaServer/OpenModules/ModArcBasic.Logic/ModArcBasic.Logic.csproj',
            'ScadaComm/OpenDrivers/DrvSimulator.Logic/DrvSimulator.Logic.csproj',
            'ScadaComm/OpenDrivers/DrvDsScadaServer.Logic/DrvDsScadaServer.Logic.csproj',
            'ScadaWeb/OpenPlugins/PlgMain/PlgMain.csproj',
            'ScadaWeb/OpenPlugins/PlgChart/PlgChart.csproj',
            'ScadaWeb/Mimics/PlgMimic/PlgMimic.csproj',
            'ScadaWeb/Mimics/PlgMimicEditor/PlgMimicEditor.csproj',
            'ScadaWeb/Mimics/PlgMimBasicComp/PlgMimBasicComp.csproj',
            'ScadaWeb/OpenPlugins/PlgWebPage/PlgWebPage.csproj',
            'ScadaWeb/OpenPlugins/PlgHelloWorld/PlgHelloWorld.csproj'
        )
        foreach ($project in $projects) {
            Invoke-DotNet @('build', $project, '-c', 'Release', '--nologo', '-v', 'quiet')
        }
        Invoke-DotNet @('publish', 'ScadaServer/ScadaServer/ScadaServerWkr/ScadaServerWkr.csproj', '-c', 'Release', '--no-build', '-o', "$runtime/ScadaServer", '-v', 'quiet')
        Invoke-DotNet @('publish', 'ScadaComm/ScadaComm/ScadaCommWkr/ScadaCommWkr.csproj', '-c', 'Release', '--no-build', '-o', "$runtime/ScadaComm", '-v', 'quiet')
        Invoke-DotNet @('publish', 'ScadaWeb/ScadaWeb/ScadaWeb/ScadaWeb.csproj', '-c', 'Release', '--no-build', '-o', "$runtime/ScadaWeb", '-v', 'quiet')

        foreach ($app in @('ScadaServer', 'ScadaComm', 'ScadaWeb')) {
            foreach ($dir in @('Config', 'Log', 'Storage', 'Temp', 'Cmd')) {
                New-Item -ItemType Directory -Path "$runtime/$app/$dir" -Force | Out-Null
            }
            Copy-Item "$repo/ScadaCommon/FileStorage/bin/Release/netstandard2.0/FileStorage.dll" "$runtime/$app"
            Copy-Item "$repo/Projects/HelloWorld/Instances/Default/$app/Config/*" "$runtime/$app/Config" -Recurse -Force
        }
        New-Item -ItemType Directory -Path "$runtime/Config" -Force | Out-Null
        Copy-Item "$repo/ScadaCommon/ScadaCommon/Config/ScadaInstanceConfig.xml" "$runtime/Config"
        New-Item -ItemType Directory -Path "$runtime/ScadaServer/Mod", "$runtime/ScadaComm/Drv" -Force | Out-Null
        Copy-Item "$repo/ScadaServer/OpenModules/ModArcBasic.Logic/bin/Release/netstandard2.0/ModArcBasic.Logic.dll" "$runtime/ScadaServer/Mod"
        foreach ($driver in @('DrvSimulator', 'DrvDsScadaServer')) {
            Copy-Item "$repo/ScadaComm/OpenDrivers/$driver.Logic/bin/Release/netstandard2.0/$driver.Logic.dll" "$runtime/ScadaComm/Drv"
        }
        $enabledPlugins = @('PlgMain', 'PlgChart', 'PlgMimic', 'PlgMimicEditor', 'PlgMimBasicComp', 'PlgWebPage', 'PlgHelloWorld')
        foreach ($plugin in $enabledPlugins) {
            $pluginDir = "$repo/ScadaWeb/OpenPlugins/$plugin"
            if (!(Test-Path $pluginDir)) { $pluginDir = "$repo/ScadaWeb/Mimics/$plugin" }
            $target = if ($plugin -eq 'PlgHelloWorld') { 'net10.0' } else { 'net8.0' }
            Get-ChildItem "$pluginDir/bin/Release/$target/Plg*.dll" | Copy-Item -Destination "$runtime/ScadaWeb" -Force
            if (Test-Path "$pluginDir/wwwroot") { Copy-Item "$pluginDir/wwwroot/*" "$runtime/ScadaWeb/wwwroot" -Recurse -Force }
            if (Test-Path "$pluginDir/lang") { Copy-Item "$pluginDir/lang/*" "$runtime/ScadaWeb/lang" -Force }
        }
        Copy-Item "$repo/LangPack/Chinese-Simplified/ScadaWeb/lang/*.zh-Hans.xml" "$runtime/ScadaWeb/lang" -Force
        Copy-Item "$repo/ScadaReport/ScadaReport/bin/Release/net8.0/ScadaReport.dll" "$runtime/ScadaWeb"
        New-Item -ItemType Directory -Path "$runtime/Views" -Force | Out-Null
        Copy-Item "$repo/Projects/HelloWorld/Views/*" "$runtime/Views" -Recurse -Force
        Copy-Item "$repo/Projects/HelloWorld/HelloWorld.rsproj" $runtime -Force
        New-Item -ItemType Directory -Path "$runtime/BaseXML" -Force | Out-Null
        Copy-Item "$repo/Projects/HelloWorld/BaseXML/*" "$runtime/BaseXML" -Force
        Invoke-DotNet @('run', '--project', 'tools/DemoSetup/DemoSetup.csproj', '-c', 'Release', '--', "$repo/Projects/HelloWorld/BaseXML", "$runtime/BaseDAT")

        [xml]$serverConfig = Get-Content "$runtime/ScadaServer/Config/ScadaServerConfig.xml"
        foreach ($module in @($serverConfig.ScadaServerConfig.Modules.Module)) {
            if ($module.code -ne 'ModArcBasic') { $null = $module.ParentNode.RemoveChild($module) }
        }
        $serverConfig.Save("$runtime/ScadaServer/Config/ScadaServerConfig.xml")
        [xml]$webConfig = Get-Content "$runtime/ScadaWeb/config/ScadaWebConfig.xml"
        foreach ($plugin in @($webConfig.ScadaWebConfig.Plugins.Plugin)) {
            if ($plugin.code -notin $enabledPlugins) { $null = $plugin.ParentNode.RemoveChild($plugin) }
        }
        $webConfig.ScadaWebConfig.GeneralOptions.ShareStats = 'false'
        $webConfig.ScadaWebConfig.GeneralOptions.DefaultCulture = 'zh-Hans'
        $webConfig.Save("$runtime/ScadaWeb/config/ScadaWebConfig.xml")
        [xml]$mimicConfig = Get-Content "$runtime/ScadaWeb/config/PlgMimic.xml"
        $mimicConfig.PlgMimic.RuntimeOptions.ScaleType = 'FitWidth'
        $mimicConfig.Save("$runtime/ScadaWeb/config/PlgMimic.xml")
    } finally { Pop-Location }
}

$entries = @()
try {
    foreach ($app in @('ScadaServer', 'ScadaComm', 'ScadaWeb')) {
        $name = if ($app -eq 'ScadaWeb') { 'ScadaWeb.exe' } else { "${app}Wkr.exe" }
        $path = Join-Path "$runtime/$app" $name
        $startOptions = @{
            FilePath = $path; WorkingDirectory = "$runtime/$app"; WindowStyle = 'Hidden'; PassThru = $true
            RedirectStandardOutput = "$runtime/$app/Log/console.log"
            RedirectStandardError = "$runtime/$app/Log/console-error.log"
        }
        if ($app -eq 'ScadaWeb') { $startOptions.ArgumentList = @('--urls', 'http://127.0.0.1:5000') }
        $process = Start-Process @startOptions
        $entries += [pscustomobject]@{ App = $app; Id = $process.Id; Path = $path }
        $entries | ConvertTo-Json | Set-Content $stateFile
        Start-Sleep -Seconds 2
        if ($process.HasExited) { throw "$app exited. See $runtime/$app/Log" }
    }
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $response = Invoke-WebRequest 'http://127.0.0.1:5000/Login' -TimeoutSec 3 -UseBasicParsing
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        } catch { Start-Sleep -Seconds 1 }
    }
    if (!$ready) { throw 'Web login page is not ready. Check the application logs.' }
    Write-Host 'HelloWorld demo: http://127.0.0.1:5000'
    Write-Host 'Login: admin / 12345'
    Write-Host 'Operator: operator / demo123; read-only: viewer / demo123'
    Write-Host "Runtime and logs: $runtime"
} catch {
    & "$PSScriptRoot/Stop-Demo.ps1"
    throw
}
