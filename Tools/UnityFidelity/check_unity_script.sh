#!/bin/sh
# Compiles an editor script (default UnityReference.cs.txt) against a Unity project's editor and compiled script
# assemblies before a batch run.
# usage: check_unity_script.sh <unity-project> [dotnet-sdk-dir] [script]
P="$1"; SDK="${2:-$(cd "$(dirname "$0")/../../../.tools/sdk" 2>/dev/null && pwd -W)}"
V=$(sed -n 's/^m_EditorVersion: //p' "$P/ProjectSettings/ProjectVersion.txt" | tr -d '\r')
U="C:/Program Files/Unity/Hub/Editor/$V/Editor/Data"
here="$(cd "$(dirname "$0")" && pwd -W)"
SCRIPT="${3:-$here/UnityReference.cs.txt}"
rsp="$TEMP/codex-fidelity-check.rsp"
csc=$(ls "$SDK"/sdk/*/Roslyn/bincore/csc.dll | head -1)
{
  echo "-nologo"; echo "-noconfig"; echo "-nostdlib"; echo "-t:library"; echo "-out:\"$TEMP/codex-fidelity-check.dll\""
  echo "-r:\"$U/NetStandard/ref/2.1.0/netstandard.dll\""; echo "-r:\"$U/Managed/UnityEditor.dll\""
  for d in "$U/Managed/UnityEngine/UnityEngine"*.dll; do echo "-r:\"$d\""; done
  echo "-define:UNITY_EDITOR"
  for d in "$P/Library/ScriptAssemblies/"*.dll; do echo "-r:\"$d\""; done
  echo "\"$SCRIPT\""
} > "$rsp"
"$SDK/dotnet.exe" "$csc" "@$rsp" 2>&1 | grep -E "error" | head -20
