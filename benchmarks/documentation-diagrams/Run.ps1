param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$graphs=@(
 @{Labels=@('LocalTasks.ReadImageAsync','PdfPageReader.OpenAsync','ILocalModelClient.RoutedChatAsync','HierarchicalReducer'); Edges=@('0>1','0>2','0>3')},
 @{Labels=@('PdfPageReader.OpenAsync','Conversion.GetPageSizes','BoundedRaster'); Edges=@('0>1','0>2')},
 @{Labels=@('PdfPageReader.RenderAsync','Conversion.ToImagesAsync','SKImage.FromBitmap','SKImage.Encode'); Edges=@('0>1','0>2','0>3')},
 @{Labels=@('PdfPageReader.ReadNextAsync','RenderAsync','MoveNextAsync'); Edges=@('0>1','0>2')},
 @{Labels=@('LocalRunProgress.Report','ReadingPdfPage','ReadingImage','ReportCounted'); Edges=@('0>1','0>2','0>3')},
 @{Labels=@('ReportCounted','ProgressMinutesLeft','Write'); Edges=@('0>1','0>2')},
 @{Labels=@('CurrentVersionFiles','VersionRequiredFiles','PdfRuntimeFiles'); Edges=@('0>1','0>2')},
 @{Labels=@('CurrentPackageArtifactFiles','CurrentVersionFiles','StableLauncherFile'); Edges=@('0>1','0>2')},
 @{Labels=@('ReleaseSigner.Pack','CurrentPackageArtifactFiles','ZipArchive'); Edges=@('0>1','0>2')},
 @{Labels=@('ReleasePackageVerifier','ValidateLocalHeaders','IsKnownPackageArtifacts'); Edges=@('0>1','0>2')},
 @{Labels=@('RetainApprovedFiles','IStagingRootLease.RetainFile','IRetainedStagingFile'); Edges=@('0>1','1>2')},
 @{Labels=@('ValidateAuthenticode','IRetainedStagingFile.Revalidate','IAuthenticodeVerifier.IsTrusted'); Edges=@('0>1','0>2')},
 @{Labels=@('LocalAiPackageInstaller.InstallAsync','LockInstalledVersion','CopyVerifiedAsync','processRunner.RunAsync'); Edges=@('0>1','0>2','0>3')},
 @{Labels=@('ValidateExactVersion','ValidateExactDirectory','VerifyFile','VerifiedPackage.Revalidate'); Edges=@('0>1','0>2','0>3')},
 @{Labels=@('PdfPageReader.DisposeAsync','rendered.DisposeAsync','input.DisposeAsync'); Edges=@('0>1','0>2')},
 @{Labels=@('ConfiguredCatalogStartupTests','BrokerClient.ExecuteAsync','DurableQueue.EnqueueAsync'); Edges=@('0>1','1>2')},
 @{Labels=@('DurableQueue.EnqueueAsync','EnterMutex','AllocateSequence','AtomicWriteJson','Directory.Move'); Edges=@('0>1','0>2','0>3','0>4')},
 @{Labels=@('DurableQueue.EnqueueAsync','ReadEntries','ReadRequest','DeduplicationKey'); Edges=@('0>1','0>2','0>3')},
 @{Labels=@('DurableQueue.LeaseNextAsync','MaintainActiveRoot','RecoverExpiredLeases','Lease'); Edges=@('0>1','0>2','0>3')},
 @{Labels=@('InstallationLayoutLease.Publish','IsKnownVersionFiles','ValidateExactVersionDirectory','Revalidate'); Edges=@('0>1','0>2','0>3')}
)
$graphs | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'graphs.json') -Encoding utf8NoBOM
$driverExecutable=Join-Path $PSScriptRoot 'bin/Release/net10.0/DiagramBenchmark.exe'
$rows=[Collections.Generic.List[object]]::new()
for($i=0;$i -lt $graphs.Count;$i++) {
 $g=$graphs[$i]
 $nodes=for($n=0;$n -lt $g.Labels.Count;$n++) {"n${n}: $($g.Labels[$n])"}
 $edges=$g.Edges | ForEach-Object { $parts=$_ -split '>'; "n$($parts[0])->n$($parts[1])" }
 $prompt="Return only one complete SVG XML document. No markdown, explanation or extra text. Draw exactly the supplied dependency graph. Root must have xmlns='http://www.w3.org/2000/svg', width='1200', height='700' and viewBox='0 0 1200 700'. Use only svg, g, rect, line, path, polygon, circle and text elements, with presentation attributes; no script, style, links, images, foreignObject or external resources. White background and black text. Font size at least 18. Each node is one g with data-symbol-id equal to its n-number and exactly one text whose content is the FULL supplied label, never the n-number. Each directed edge is a line or path with data-from and data-to equal to its source and target n-numbers, plus a visible arrowhead made from a polygon. Fit every full label inside its node, and keep nodes apart. Do not add or remove nodes or edges. Nodes: $($nodes -join '; '). Edges: $($edges -join ', ')."
 foreach($model in @('qwen3.5:9b','gpt-oss:20b')) {
  $tag=$model.Replace(':','-').Replace('.','-')
  $path=Join-Path $OutputDirectory ("{0:D2}-{1}.log" -f ($i+1),$tag)
  Write-Output "Diagram $($i+1)/20, model $model started."
  $promptPath=[IO.Path]::ChangeExtension($path,'.prompt.txt')
  $jobPath=[IO.Path]::ChangeExtension($path,'.job.json')
  [IO.File]::WriteAllText($promptPath,$prompt,[Text.UTF8Encoding]::new($false))
  & $driverExecutable $model $promptPath $jobPath *> $path
  $code=$LASTEXITCODE
  $raw=[IO.File]::ReadAllText($path)
  $notice=($raw -split '\r?\n' | Where-Object {$_ -match '🔧 Локально:|Locally:'}) -join "`n"
  if($notice) { Write-Output $notice }
  if($code -ne 0) { Write-Output "Run refused or failed: exit $code, log $path"; Set-Content -LiteralPath (Join-Path $OutputDirectory 'stopped.txt') -Value $path; exit $code }
  $job=[IO.File]::ReadAllText($jobPath) | ConvertFrom-Json
  $answer=$job.Value.Trim()
  $fail=[Collections.Generic.List[string]]::new()
  try {
   $settings=[Xml.XmlReaderSettings]::new(); $settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit; $settings.XmlResolver=$null; $settings.MaxCharactersInDocument=100000
   $reader=[Xml.XmlReader]::Create([IO.StringReader]::new($answer),$settings)
   try {$xml=[Xml.XmlDocument]::new(); $xml.XmlResolver=$null; $xml.Load($reader)} finally {$reader.Dispose()}
   if($xml.DocumentElement.LocalName -ne 'svg' -or $xml.DocumentElement.NamespaceURI -ne 'http://www.w3.org/2000/svg') {$fail.Add('svg namespace')}
   $elements=@($xml.SelectNodes('//*'))
   foreach($element in $elements) {if($element.LocalName -notin @('svg','g','rect','line','path','polygon','circle','text')) {$fail.Add('forbidden element')}; foreach($attribute in $element.Attributes) {if($attribute.Name -match '^on|href|style' -or $attribute.Value -match 'url\(') {$fail.Add('external or active content')}}}
   $seen=@($xml.SelectNodes('//*[@data-symbol-id]'))
   if($seen.Count -ne $g.Labels.Count) {$fail.Add('node count')}
   for($n=0;$n -lt $g.Labels.Count;$n++) { $node=$xml.SelectSingleNode("//*[@data-symbol-id='n$n']"); if($null -eq $node) {$fail.Add("missing n$n");continue}; $texts=@($node.SelectNodes('.//*[local-name()="text"]')); if($texts.Count -ne 1 -or $texts[0].InnerText -cne $g.Labels[$n]) {$fail.Add("label n$n")} }
   $actual=@($xml.SelectNodes('//*[(local-name()="line" or local-name()="path") and (@data-from or @data-to)]') | ForEach-Object {$_.GetAttribute('data-from')+'->'+$_.GetAttribute('data-to')} | Sort-Object)
   if(($actual -join ';') -cne (($edges | Sort-Object) -join ';')) {$fail.Add('edges')}
   if(@($xml.SelectNodes('//*[local-name()="text"]')).Count -ne $g.Labels.Count) {$fail.Add('extra text')}
  } catch {$fail.Add('XML: '+$_.Exception.Message)}
  [IO.File]::WriteAllText([IO.Path]::ChangeExtension($path,'.svg'),$answer,[Text.UTF8Encoding]::new($false))
  $rows.Add([pscustomobject]@{Diagram=$i+1;Model=$model;Passed=($fail.Count -eq 0);Failures=@($fail);Log=$path;Notice=$notice})
  $rows | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'corrected-results.json') -Encoding utf8NoBOM
  Write-Output "Validation: $($fail.Count) failure(s)."
 }
}
Write-Output 'A/B finished: twenty diagrams per model.'