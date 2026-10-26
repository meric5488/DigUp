# Télécharge EmbeddingGemma (ONNX) dans %LOCALAPPDATA%\Fouineur\models\embeddinggemma.
# Si Hugging Face demande une authentification (modèle soumis à l'acceptation de la licence Gemma),
# acceptez la licence sur la page du modèle puis passez un jeton : -Jeton hf_xxx
param([string]$Jeton, [switch]$Quantifie)
$ErrorActionPreference = 'Stop'
$depot = 'https://huggingface.co/onnx-community/embeddinggemma-300m-ONNX/resolve/main'
$dest = Join-Path $env:LOCALAPPDATA 'Fouineur\models\embeddinggemma'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$fichiers = if ($Quantifie) { @('onnx/model_q4.onnx', 'onnx/model_q4.onnx_data', 'tokenizer.model') }
            else { @('onnx/model.onnx', 'onnx/model.onnx_data', 'tokenizer.model') }
$entetes = @{}
if ($Jeton) { $entetes['Authorization'] = "Bearer $Jeton" }

foreach ($f in $fichiers) {
    $cible = Join-Path $dest (Split-Path $f -Leaf)
    Write-Host "Téléchargement de $f…"
    try { Invoke-WebRequest -Uri "$depot/$f" -OutFile $cible -Headers $entetes -UseBasicParsing }
    catch {
        if ($f -like '*_data') { Write-Host "  (pas de fichier $f, ignoré)"; continue }
        throw
    }
}
Write-Host "Modèle installé dans $dest. Redémarrez Fouineur ou rouvrez les réglages." -ForegroundColor Green
