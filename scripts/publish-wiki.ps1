# Maintainer-only publication. No credentials are accepted as script parameters.
[CmdletBinding(DefaultParameterSetName='Preview')]
param(
    [Parameter(Mandatory, ParameterSetName='Preview')][string]$PreviewDirectory,
    [Parameter(Mandatory, ParameterSetName='Publish')][switch]$Publish
)
$ErrorActionPreference='Stop'
$repository='MrAliev/local-ai-toolkit'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source=Join-Path $root 'docs/wiki'
$top=(& git -C $root rev-parse --show-toplevel)
if($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($top.Trim()) -ne $root){ throw 'Run the script from its source repository, not a nested checkout.' }
$sha=(& git -C $root rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0){ throw 'Cannot resolve source commit.' }
if($Publish){
    $origin=(& git -C $root remote get-url origin).Trim().TrimEnd('/') -replace '\.git$',''
    if($LASTEXITCODE -ne 0 -or $origin -notin @("https://github.com/$repository","git@github.com:$repository")) { throw 'Publication is restricted to the canonical project origin.' }
    $dirty=& git -C $root status --porcelain
    if($LASTEXITCODE -ne 0 -or $dirty){ throw 'Publish requires a clean, committed source tree. Use preview first.' }
    & git -C $root fetch origin main
    if($LASTEXITCODE -ne 0){ throw 'Cannot verify origin/main.' }
    $main=(& git -C $root rev-parse origin/main).Trim()
    if($sha -ne $main){ throw 'Publish only the exact origin/main commit after CI has passed.' }
    $target=Join-Path ([IO.Path]::GetTempPath()) ('localai-wiki-publish-'+[guid]::NewGuid().ToString('N'))
    & git clone "https://github.com/$repository.wiki.git" $target
    if($LASTEXITCODE -ne 0){ throw 'Wiki clone failed. Enable Wiki and create its Home page on GitHub first. No pages were pushed.' }
} else {
    $target=[IO.Path]::GetFullPath($PreviewDirectory)
    if($target.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $target -eq $root){ throw 'Preview directory must be outside the source repository.' }
    $marker=Join-Path $target '.localai-wiki-preview'
    if((Test-Path -LiteralPath $target) -and (Get-ChildItem -LiteralPath $target -Force | Select-Object -First 1)){
        if(-not (Test-Path -LiteralPath $marker) -or [IO.File]::ReadAllText($marker).Trim() -ne $repository){ throw 'Preview directory is not empty and is not owned by this generator.' }
    }
    [void][IO.Directory]::CreateDirectory($target)
    [IO.File]::WriteAllText($marker,$repository,[Text.UTF8Encoding]::new($false))
}
$utf8=[Text.UTF8Encoding]::new($false)
$pages=Get-ChildItem -LiteralPath $source -Filter '*.md' -File | Sort-Object Name
foreach($page in $pages){
    $pageName=$page.Name
    $text=[IO.File]::ReadAllText($page.FullName)
    $text=[regex]::Replace($text,'(?<prefix>!?\[[^\]\r\n]*\]\()(?<target>[^)\s]+)(?<suffix>\))',{
        param($match)
        $href=$match.Groups['target'].Value
        if($href -match '^(https?://|mailto:|#)'){ return $match.Value }
        $uri=[uri]::new([uri]("https://documentation.invalid/docs/wiki/"+$pageName),$href)
        $path=$uri.AbsolutePath
        if($path.StartsWith('/docs/wiki/')){
            $relative=$path.Substring('/docs/wiki/'.Length)
            if($relative.Contains('/') -or -not $relative.EndsWith('.md')){ throw "Unsupported Wiki target: $href" }
            if(-not (Test-Path -LiteralPath (Join-Path $source ([uri]::UnescapeDataString($relative))))){ throw "Missing Wiki source: $href" }
            $resolved=$relative.Substring(0,$relative.Length-3)+$uri.Fragment
        } elseif($path.StartsWith('/docs/')){
            $resolved='https://mraliev.github.io/local-ai-toolkit/'+($path.Substring(6) -replace '\.md$','.html')+$uri.Fragment
        } else {
            $resolved="https://github.com/$repository/blob/main"+$path+$uri.Fragment
        }
        return $match.Groups['prefix'].Value+$resolved+$match.Groups['suffix'].Value
    })
    $russian=$pageName.EndsWith('.ru.md')
    $shortSha=$sha.Substring(0,12)
    $sourceLink="[$shortSha](https://github.com/$repository/tree/$sha/docs/wiki)"
    if($Publish){
        $publicationStamp=if($russian){ "Исходники: $sourceLink. Изменения вносятся через pull request." }
            else { "Source: $sourceLink. Changes via pull request." }
    } else {
        $publicationStamp=if($russian){ "Предварительный просмотр рабочей копии на основе $shortSha. Эта версия ещё не опубликована." }
            else { "Working-tree preview based on $shortSha. This version is unpublished." }
    }
    $text+="`n`n---`n`n$publicationStamp`n"
    [IO.File]::WriteAllText((Join-Path $target $pageName),$text,$utf8)
}
$sidebar=@'
## Local AI Toolkit

**English**
- [Home](Home)
- [First repository](Getting-started)
- [Architecture](Architecture)
- [Token estimates](Token-estimates)
- [Trust & security](Trust-and-security)
- [Community](Community)

**Русский**
- [Главная](Home.ru)
- [Первый репозиторий](Getting-started.ru)
- [Архитектура](Architecture.ru)
- [Оценка токенов](Token-estimates.ru)
- [Доверие и безопасность](Trust-and-security.ru)
- [Сообщество](Community.ru)

[Interactive portal / Интерактивный портал](https://mraliev.github.io/local-ai-toolkit/)
'@
[IO.File]::WriteAllText((Join-Path $target '_Sidebar.md'),$sidebar,$utf8)
$site='https://mraliev.github.io/local-ai-toolkit'
$footer="MIT · [Source / Исходники](https://github.com/$repository) · [Privacy]($site/privacy.html) · [Конфиденциальность]($site/privacy.ru.html) · [Code signing policy]($site/code-signing-policy.html) · [Политика подписи]($site/code-signing-policy.ru.html)`n"
[IO.File]::WriteAllText((Join-Path $target '_Footer.md'),$footer,$utf8)
if($Publish){
    & git -C $target add -- ($pages.Name) '_Sidebar.md' '_Footer.md'
    if($LASTEXITCODE -ne 0){ throw 'Cannot stage managed Wiki pages.' }
    & git -C $target diff --cached --quiet
    if($LASTEXITCODE -eq 0){ Write-Output 'Wiki already matches the source commit.'; exit 0 }
    & git -C $target -c user.name='Anton Aliev' -c user.email='46073415+MrAliev@users.noreply.github.com' commit -m "docs: publish handbook from $($sha.Substring(0,12))"
    if($LASTEXITCODE -ne 0){ throw 'Wiki commit failed. No push performed.' }
    & git -C $target push origin HEAD
    if($LASTEXITCODE -ne 0){ throw 'Wiki push failed. The local checkout is preserved for inspection.' }
}
Write-Output "Wiki pages: $($pages.Count+2); directory: $target; source: $sha"
