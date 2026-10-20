# One release identity drives payload paths, source links and published assets.
function Get-ReleaseIdentity([string]$Version) {
    $numeric = '(0|[1-9][0-9]*)'
    $identifier = '[0-9A-Za-z-]+'
    $pattern = "\A$numeric\.$numeric\.$numeric(?:-($identifier(?:\.$identifier)*))?(?:\+($identifier(?:\.$identifier)*))?\z"
    if ($Version -cnotmatch $pattern) { throw "Expected a SemVer release version: '$Version'." }
    $pre = $Matches[4]
    if ($pre) {
        foreach ($part in $pre.Split('.')) {
            if ($part -cmatch '\A[0-9]+\z' -and $part.Length -gt 1 -and $part[0] -eq '0') {
                throw "Numeric prerelease identifiers cannot have leading zeroes: '$Version'."
            }
        }
    }
    $root = "DotCharter-v$Version-win-x64"
    [pscustomobject]@{
        Version = $Version
        Tag = "v$Version"
        RootName = $root
        ZipName = "$root.zip"
        SourceUrl = "https://github.com/SomeeName/DotCharter/tree/v$Version"
    }
}

function Assert-ReleaseVersionSelfTest {
    $identity = Get-ReleaseIdentity '1.0.0-beta.1'
    if ($identity.Version -cne '1.0.0-beta.1' -or
        $identity.Tag -cne 'v1.0.0-beta.1' -or
        $identity.RootName -cne 'DotCharter-v1.0.0-beta.1-win-x64' -or
        $identity.ZipName -cne 'DotCharter-v1.0.0-beta.1-win-x64.zip' -or
        $identity.SourceUrl -cne 'https://github.com/SomeeName/DotCharter/tree/v1.0.0-beta.1') {
        throw 'Prerelease asset identity did not retain the complete version.'
    }
}

Export-ModuleMember -Function Get-ReleaseIdentity, Assert-ReleaseVersionSelfTest
