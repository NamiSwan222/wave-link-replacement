# Moves Equalizer APO on a capture endpoint from the stream slot (SFX, skipped by raw-mode
# streams such as Discord's) to the endpoint slot (EFX, applied to every stream incl. raw).
# Must run as SYSTEM (the key's owner): it temporarily grants itself write access, changes the
# values, then restores the original permissions exactly. Use run-as-system.ps1 to launch it.
# -Undo puts Equalizer APO back in the stream slot (the state Device Selector created).
param([string]$EndpointGuid = '{d3da57cc-220c-47ef-ae6d-eba1169fcc04}', [switch]$Undo, [string]$Log = "$env:TEMP\set-mic-effect-slot.log")

$ErrorActionPreference = 'Stop'
$path = "SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture\$EndpointGuid\FxProperties"
$sfxClsid = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5'   # PKEY_FX_StreamEffectClsid
$efxClsid = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7'   # PKEY_FX_EndpointEffectClsid
$sfxModes = '{d3993a3f-99c2-4402-b5ec-a92a0367664b},5'   # PKEY_SFX_ProcessingModes_Supported_For_Streaming
$efxModes = '{d3993a3f-99c2-4402-b5ec-a92a0367664b},7'   # PKEY_EFX_ProcessingModes_Supported_For_Streaming
$eapoSfx = '{EACD2258-FCAC-4FF4-B36D-419E924A6D79}'      # Equalizer APO stream effect
$eapoEfx = '{EC1CC9CE-FAED-4822-828A-82A81A6F018F}'      # Equalizer APO endpoint effect
$default = '{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}'      # AUDIO_SIGNALPROCESSINGMODE_DEFAULT
"start $(Get-Date) as $([Security.Principal.WindowsIdentity]::GetCurrent().Name) undo=$Undo" | Out-File $Log

$hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
$rights = [Security.AccessControl.RegistryRights]
$check = [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree
$key = $hklm.OpenSubKey($path, $check, $rights::ReadPermissions -bor $rights::ChangePermissions)
$acl = $key.GetAccessControl()
$originalSddl = $acl.GetSecurityDescriptorSddlForm('Access')
"original permissions saved" | Out-File $Log -Append
try {
  $me = [Security.Principal.WindowsIdentity]::GetCurrent().User
  $acl.AddAccessRule((New-Object Security.AccessControl.RegistryAccessRule($me, 'SetValue, QueryValues', 'Allow')))
  $key.SetAccessControl($acl); $key.Close()

  $w = $hklm.OpenSubKey($path, $check, $rights::SetValue -bor $rights::QueryValues)
  $multi = [Microsoft.Win32.RegistryValueKind]::MultiString
  if ($Undo) {
    $w.SetValue($sfxClsid, $eapoSfx, [Microsoft.Win32.RegistryValueKind]::String)
    $w.SetValue($sfxModes, [string[]]@($default), $multi)
    foreach ($n in $efxClsid, $efxModes) { $w.DeleteValue($n, $false) }
  } else {
    $w.SetValue($efxClsid, $eapoEfx, [Microsoft.Win32.RegistryValueKind]::String)
    $w.SetValue($efxModes, [string[]]@($default), $multi)
    foreach ($n in $sfxClsid, $sfxModes) { $w.DeleteValue($n, $false) }
  }
  foreach ($n in $sfxClsid, $sfxModes, $efxClsid, $efxModes) { "  $n = $(($w.GetValue($n)) -join ' | ')" | Out-File $Log -Append }
  $w.Close()
} catch { "ERROR: $_" | Out-File $Log -Append }
finally {
  $k2 = $hklm.OpenSubKey($path, $check, $rights::ReadPermissions -bor $rights::ChangePermissions)
  $restore = $k2.GetAccessControl(); $restore.SetSecurityDescriptorSddlForm($originalSddl, 'Access'); $k2.SetAccessControl($restore)
  "permissions restored: $($k2.GetAccessControl().GetSecurityDescriptorSddlForm('Access') -eq $originalSddl)" | Out-File $Log -Append
  $k2.Close()
}
