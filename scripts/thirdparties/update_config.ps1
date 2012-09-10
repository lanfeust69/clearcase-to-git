$thirdpartyFile = ls thirdparty.config

$thirdparty = New-Object System.Xml.XmlDocument
$thirdparty.Load($thirdpartyFile)
$changed = $false

foreach ($partialFile in (ls working/*/*.config))
{
    Write-Host "Merging $partialFile"
    $partial = New-Object System.Xml.XmlDocument
    $partial.Load($partialFile)
    # handle no real labels (/main/LATEST)
    $labels = $partial.ThirdPartyModule.SelectSingleNode("Labels");
    if ($labels.ChildNodes.Count -eq 0) { continue }

    $moduleName = $partial.ThirdPartyModule.Name

    $existingModule = $thirdparty.ThirdPartyConfig.Modules.SelectSingleNode("ThirdPartyModule[Name='$moduleName']")
    if ($existingModule -eq $null)
    {
        $thirdparty.ThirdPartyConfig.Modules.AppendChild($thirdparty.ImportNode($partial.DocumentElement, $true))
        $changed = $true
    }
    else
    {
        foreach ($mapping in $labels.ChildNodes)
        {
            $label = $mapping.Label
            $existingLabel = $existingModule.Labels.SelectSingleNode("LabelMapping[Label='$label']")
            if ($existingLabel -eq $null)
            {
                $existingModule.Labels.AppendChild($thirdparty.ImportNode($mapping, $true))
                $changed = $true
            }
            elseif ($existingLabel.Commit -ne $mapping.Commit)
            {
                $existingLabel.Commit = $mapping.Commit
                $changed = $true
            }
        }
    }
}

if ($changed)
{
    $thirdparty.Save($thirdpartyFile.DirectoryName + "\thirdparty.new.config")
}
else
{
    Write-Host "No change found"
}
