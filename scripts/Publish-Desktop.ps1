param(
    [ValidateSet('win-x64', 'osx-arm64', 'osx-x64', 'linux-x64')]
    [string]$Runtime = 'win-x64',
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $projectRoot 'PodcastBatchCleaner.CrossPlatform/PodcastBatchCleaner.CrossPlatform.csproj'
$output = Join-Path $projectRoot "release/Avalonia/$Runtime"
if ($Runtime.StartsWith('osx-')) {
    $bundle = Join-Path $output 'PodcastBatchCleaner.app'
    $contents = Join-Path $bundle 'Contents'
    $output = Join-Path $contents 'MacOS'
    New-Item -ItemType Directory -Force -Path $output | Out-Null
}
& $Dotnet publish $project -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false -m:1 -o $output
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
if ($Runtime -eq 'win-x64') {
    $ffmpegSource = Join-Path $projectRoot 'tools/FFmpeg'
    $ffmpegTarget = Join-Path $output 'tools/FFmpeg'
    New-Item -ItemType Directory -Force -Path $ffmpegTarget | Out-Null
    foreach ($name in @('ffmpeg.exe', 'ffprobe.exe', 'ffplay.exe', 'LICENSE', 'README.md')) {
        $source = Join-Path $ffmpegSource $name
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $ffmpegTarget }
    }
}
if ($Runtime.StartsWith('osx-')) {
    $settings = [ordered]@{
        CFBundleName = 'PodcastBatchCleaner'
        CFBundleDisplayName = 'PodcastBatchCleaner'
        CFBundleIdentifier = 'com.podcastbatchcleaner.desktop'
        CFBundleExecutable = 'PodcastBatchCleaner.CrossPlatform'
        CFBundlePackageType = 'APPL'
        CFBundleShortVersionString = '1.0.0'
        CFBundleVersion = '1'
        NSHighResolutionCapable = $true
    }
    $xml = [Xml.XmlWriter]::Create((Join-Path $contents 'Info.plist'), [Xml.XmlWriterSettings]@{ Indent = $true; Encoding = [Text.UTF8Encoding]::new($false) })
    try {
        $xml.WriteStartDocument()
        $xml.WriteDocType('plist', '-//Apple//DTD PLIST 1.0//EN', 'http://www.apple.com/DTDs/PropertyList-1.0.dtd', $null)
        $xml.WriteStartElement('plist'); $xml.WriteAttributeString('version', '1.0'); $xml.WriteStartElement('dict')
        foreach ($entry in $settings.GetEnumerator()) {
            $xml.WriteElementString('key', $entry.Key)
            if ($entry.Value -is [bool]) { $xml.WriteStartElement('true'); $xml.WriteEndElement() }
            else { $xml.WriteElementString('string', [string]$entry.Value) }
        }
        $xml.WriteEndElement(); $xml.WriteEndElement(); $xml.WriteEndDocument()
    } finally { $xml.Dispose() }
    if ($IsMacOS) { & chmod +x (Join-Path $output 'PodcastBatchCleaner.CrossPlatform') }
    Write-Output "App bundle: $bundle (requires macOS FFmpeg; not code-signed)"
} else { Write-Output "Desktop output: $output" }
