# Diagnostic de connexion à l'API REST de GLPI.
# Teste plusieurs façons d'ouvrir une session et affiche seulement le code de réponse (aucun jeton affiché).
# Utilisation : clic droit > Exécuter avec PowerShell, ou : powershell -ExecutionPolicy Bypass -File scripts\test-glpi.ps1

param([string]$Url = "http://10.255.252.12")

function Lire-Secret([string]$invite) {
    $s = Read-Host $invite -AsSecureString
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b) }
}

$Url = $Url.TrimEnd('/')
$user = (Lire-Secret "Jeton d'API utilisateur").Trim()
$app  = (Lire-Secret "App-Token (laisser vide si aucun)").Trim()
$api  = "$Url/apirest.php"
"Adresse $api, jeton utilisateur $($user.Length) caractères, App-Token $(if ($app) { "$($app.Length) caractères" } else { 'absent' })"
$u = [uri]::EscapeDataString($user); $a = [uri]::EscapeDataString($app)
$appQ = if ($app) { "&app_token=$a" } else { "" }

$essais = @(
    @{ Nom = "1. Tout en paramètres (comme le navigateur)";          Url = "$api/initSession?user_token=$u$appQ"; H = @{} },
    @{ Nom = "2. Paramètres + Accept: application/json";              Url = "$api/initSession?user_token=$u$appQ"; H = @{ Accept = "application/json" } },
    @{ Nom = "3. Paramètres + User-Agent Fouineur";                    Url = "$api/initSession?user_token=$u$appQ"; H = @{ "User-Agent" = "Fouineur/2.0" } },
    @{ Nom = "4. Paramètres + get_full_session=false";                 Url = "$api/initSession?get_full_session=false&user_token=$u$appQ"; H = @{} },
    @{ Nom = "5. Jeton utilisateur en en-tête, App-Token en paramètre"; Url = "$api/initSession?x=1$appQ"; H = @{ Authorization = "user_token $user" } },
    @{ Nom = "6. Jeton en paramètre, App-Token en en-tête";            Url = "$api/initSession?user_token=$u"; H = @{ "App-Token" = $app } },
    @{ Nom = "7. Tout en en-têtes (méthode officielle)";              Url = "$api/initSession"; H = @{ Authorization = "user_token $user"; "App-Token" = $app } },
    @{ Nom = "8. Via api.php/v1, tout en paramètres";                  Url = "$Url/api.php/v1/initSession?user_token=$u$appQ"; H = @{} }
)

Add-Type -AssemblyName System.Net.Http
foreach ($e in $essais) {
    $h = New-Object System.Net.Http.HttpClientHandler
    $h.UseCookies = $false
    $c = New-Object System.Net.Http.HttpClient($h)
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $e.Url)
    foreach ($k in $e.H.Keys) { if ($e.H[$k]) { [void]$req.Headers.TryAddWithoutValidation($k, $e.H[$k]) } }
    try {
        $r = $c.SendAsync($req).GetAwaiter().GetResult()
        $txt = $r.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $resume = if ($txt -match '"session_token"') { "SESSION OUVERTE" }
                  elseif ($txt.TrimStart().StartsWith("<")) { "page HTML (erreur GLPI)" }
                  else { $txt.Substring(0, [Math]::Min(120, $txt.Length)) }
        "{0,-60} {1} {2}" -f $e.Nom, [int]$r.StatusCode, $resume
        if ($txt -match '"session_token":"([^"]+)"') {
            # On referme la session ouverte pour le test.
            $k = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, "$api/killSession?session_token=$([uri]::EscapeDataString($Matches[1]))$appQ")
            [void]$c.SendAsync($k).GetAwaiter().GetResult()
        }
    } catch { "{0,-60} ÉCHEC RÉSEAU : {1}" -f $e.Nom, $_.Exception.InnerException.Message }
    $c.Dispose()
}
Write-Host ""
Read-Host "Copiez ces lignes pour Claude, puis Entrée pour fermer"
