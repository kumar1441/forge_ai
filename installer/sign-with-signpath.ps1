param(
    [string]$MsiPath = "",
    [string]$OutPath = "",
    [int]$TimeoutSeconds = 600,
    [int]$PollSeconds = 10
)
$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$apiToken = $env:SIGNPATH_API_TOKEN
$organizationId = $env:SIGNPATH_ORGANIZATION_ID
if ([string]::IsNullOrWhiteSpace($organizationId)) {
    $organizationId = $env:SIGNPATH_ORG_ID
}
$projectSlug = $env:SIGNPATH_PROJECT_SLUG
$policySlug = $env:SIGNPATH_SIGNING_POLICY_SLUG
$artifactConfigSlug = $env:SIGNPATH_ARTIFACT_CONFIGURATION_SLUG
$apiUrl = $env:SIGNPATH_API_URL
if ([string]::IsNullOrWhiteSpace($apiUrl)) {
    $apiUrl = "https://app.signpath.io/Api"
}
$description = $env:SIGNPATH_DESCRIPTION
if ([string]::IsNullOrWhiteSpace($apiToken)) {
    throw "SignPath signing is not configured: environment variable SIGNPATH_API_TOKEN is missing or empty. Set it to the SignPath CI user API token. See installer/signpath/README.md. Nothing was signed and no output file was written."
}
if ([string]::IsNullOrWhiteSpace($organizationId)) {
    throw "SignPath signing is not configured: environment variable SIGNPATH_ORGANIZATION_ID is missing or empty. Set it to the SignPath organization id from the signing policy page. See installer/signpath/README.md. Nothing was signed and no output file was written."
}
if ([string]::IsNullOrWhiteSpace($projectSlug)) {
    throw "SignPath signing is not configured: environment variable SIGNPATH_PROJECT_SLUG is missing or empty. Set it to the SignPath project slug. See installer/signpath/README.md. Nothing was signed and no output file was written."
}
if ([string]::IsNullOrWhiteSpace($policySlug)) {
    throw "SignPath signing is not configured: environment variable SIGNPATH_SIGNING_POLICY_SLUG is missing or empty. Set it to the SignPath signing policy slug. See installer/signpath/README.md. Nothing was signed and no output file was written."
}
if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    $distDir = Join-Path $scriptDir "..\dist"
    $candidates = @(Get-ChildItem -LiteralPath $distDir -Filter "*.msi" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
    if ($candidates.Count -eq 0) {
        throw "No input MSI found in $distDir. Build one first with installer/build-msi.ps1, or pass -MsiPath explicitly. Nothing was signed and no output file was written."
    }
    $MsiPath = $candidates[0].FullName
}
$resolvedMsi = [System.IO.Path]::GetFullPath($MsiPath)
if (-not (Test-Path -LiteralPath $resolvedMsi)) {
    throw "Input MSI not found: $resolvedMsi. Nothing was signed and no output file was written."
}
if ([string]::IsNullOrWhiteSpace($OutPath)) {
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedMsi)
    $outDir = [System.IO.Path]::GetDirectoryName($resolvedMsi)
    $OutPath = Join-Path $outDir ($baseName + ".signed.msi")
}
$resolvedOut = [System.IO.Path]::GetFullPath($OutPath)
$apiBase = $apiUrl.TrimEnd("/") + "/v1/" + $organizationId
$submitUri = $apiBase + "/SigningRequests/SubmitWithArtifact"
$headers = @{ Authorization = ("Bearer " + $apiToken) }
if ([string]::IsNullOrWhiteSpace($description)) {
    $description = ("Forge MSI signing request for " + [System.IO.Path]::GetFileName($resolvedMsi))
}
$form = @{
    projectSlug = $projectSlug
    signingPolicySlug = $policySlug
    artifact = Get-Item -LiteralPath $resolvedMsi
    description = $description
}
if (-not ([string]::IsNullOrWhiteSpace($artifactConfigSlug))) {
    $form["artifactConfigurationSlug"] = $artifactConfigSlug
}
Write-Host ("Submitting " + $resolvedMsi + " to SignPath ...")
$submitResponse = Invoke-RestMethod -Method Post -Uri $submitUri -Headers $headers -Form $form -ResponseHeadersVariable submitHeaders
$signingRequestId = $null
if ($null -ne $submitResponse) {
    $signingRequestId = $submitResponse.signingRequestId
    if ([string]::IsNullOrWhiteSpace($signingRequestId)) {
        $signingRequestId = $submitResponse.id
    }
}
if ([string]::IsNullOrWhiteSpace($signingRequestId) -and ($null -ne $submitHeaders)) {
    $location = $submitHeaders["Location"]
    if ($location) {
        $locationText = [string]$location
        $signingRequestId = $locationText.TrimEnd("/").Split("/")[-1]
    }
}
if ([string]::IsNullOrWhiteSpace($signingRequestId)) {
    throw "SignPath submission did not return a signing request id. The unsigned input was left untouched and no file was labelled signed."
}
Write-Host ("Signing request submitted: " + $signingRequestId)
$statusUri = $apiBase + "/SigningRequests/" + $signingRequestId + "/Status"
$signedUri = $apiBase + "/SigningRequests/" + $signingRequestId + "/SignedArtifact"
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$finalStatus = $null
while ($true) {
    $statusDoc = Invoke-RestMethod -Method Get -Uri $statusUri -Headers $headers
    $state = [string]$statusDoc.status
    $workflow = [string]$statusDoc.workflowStatus
    Write-Host ("SignPath status: " + $state + " / " + $workflow)
    if ($statusDoc.isFinalStatus) {
        $finalStatus = $statusDoc
        break
    }
    if ((Get-Date) -gt $deadline) {
        throw ("Timed out after " + $TimeoutSeconds + " seconds waiting for SignPath request " + $signingRequestId + ". The request may still complete in the SignPath dashboard. The unsigned input was left untouched and no file was labelled signed.")
    }
    Start-Sleep -Seconds $PollSeconds
}
if ([string]$finalStatus.status -ne "Completed") {
    throw ("SignPath signing request " + $signingRequestId + " ended with status " + [string]$finalStatus.status + ". The unsigned input was left untouched and no file was labelled signed.")
}
Write-Host "Signing completed. Downloading signed artifact ..."
Invoke-RestMethod -Method Get -Uri $signedUri -Headers $headers -OutFile $resolvedOut
$outItem = Get-Item -LiteralPath $resolvedOut -ErrorAction SilentlyContinue
if (($null -eq $outItem) -or ($outItem.Length -le 0)) {
    throw "Downloaded signed artifact is missing or empty. No file was labelled signed."
}
Write-Host ("Signed MSI written to: " + $resolvedOut)
Write-Host "Verify the signature with Get-AuthenticodeSignature -FilePath and confirm Status is Valid."
