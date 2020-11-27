
param (
    [Parameter(Mandatory=$true)]
    [string]$Version
)

# prepare Directory.Build.props from templates
$PropsFileName = "Directory.Build.props"
$FileContent = Get-Content "$Env:ProjectRoot\build\templates\$PropsFileName.template" -Raw
$FileContent -replace "{VERSION}","$Version" | Set-Content -Path "$Env:ProjectRoot\$PropsFileName"

# build and publish
dotnet build --configuration Release
dotnet publish --configuration Release

$PublishFolderName = "$Env:ProjectRoot\bin\Release\netcoreapp3.1\publish"

# prepare nuspec from templates
$NuspecFileName = "SAFEEXCHANGE.nuspec"
$FileContent = Get-Content "$Env:ProjectRoot\build\templates\$NuspecFileName.template" -Raw
$FileContent -replace "{VERSION}","$Version" | Set-Content -Path "$PublishFolderName\$NuspecFileName"

# pack nuget
Push-Location $PublishFolderName
nuget pack
Pop-Location
