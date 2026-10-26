@echo off
rem Compile Fouineur et ecrit le resultat dans compilation.log (a la racine du projet).
chcp 65001 >nul
cd /d "%~dp0.."
(
  echo === dotnet --list-sdks ===
  dotnet --list-sdks
  echo === sources NuGet ===
  dotnet nuget list source
  echo === tests ===
  dotnet run --project tests\Fouineur.Tests -c Release
  echo === build application ===
  dotnet build src\Fouineur.App -c Release -nologo -v q
) > compilation.log 2>&1
echo FIN>> compilation.log
