# MSI_ECO_FAN_DIAG_READONLY.ps1
# Diagnóstico SOLO LECTURA para MSI Center / Embedded Controller.
# No usa Set_Data, Set_EC, Set_Fan ni ninguna operación de escritura.
# Ejecutar desde PowerShell como Administrador.

$ErrorActionPreference = "Stop"

function Out-Line([string]$Text = "") {
    $script:Lines.Add($Text) | Out-Null
    Write-Host $Text
}

function Get-EcFirmwareText($Inst) {
    try {
        $r = Invoke-CimMethod -InputObject $Inst -MethodName Get_EC
        return ($r | Format-List * | Out-String).Trim()
    } catch {
        return "Get_EC_ERROR: $($_.Exception.Message)"
    }
}

function Read-EcByte($Inst, [byte]$Address) {
    $buf = New-Object byte[] 32
    $buf[0] = $Address

    $pkg = New-CimInstance `
        -Namespace "root\wmi" `
        -ClassName "Package_32" `
        -ClientOnly `
        -Property @{ Bytes = $buf }

    $r = Invoke-CimMethod `
        -InputObject $Inst `
        -MethodName "Get_Data" `
        -Arguments @{ Data = $pkg }

    $bytes = $null

    if ($null -ne $r.Data -and $null -ne $r.Data.Bytes) {
        $bytes = $r.Data.Bytes
    } elseif ($null -ne $r.Bytes) {
        $bytes = $r.Bytes
    }

    if ($null -eq $bytes -or $bytes.Count -lt 2) {
        throw "Get_Data devolvió un formato inesperado en 0x{0:X2}" -f $Address
    }

    [PSCustomObject]@{
        Address = ("0x{0:X2}" -f $Address)
        OkFlag  = ("0x{0:X2}" -f [byte]$bytes[0])
        Value   = ("0x{0:X2}" -f [byte]$bytes[1])
        Decimal = [int][byte]$bytes[1]
    }
}

function Capture-State($Inst, [string]$Label) {
    Out-Line
    Out-Line ("================ {0} ================" -f $Label)
    Out-Line ("Timestamp: {0}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))

    # Direcciones de interés conocidas en varias familias MSI.
    # Son SOLO lecturas. Si una dirección no aplica a este firmware,
    # su valor se tratará únicamente como dato bruto, no como significado confirmado.
    $addresses = @(
        0x34, 0x68, 0x71, 0x80, 0x89, 0x98,
        0xC9, 0xCB, 0xD2, 0xD4, 0xD7, 0xEB, 0xF2, 0xF4, 0xEF
    )

    foreach ($a in $addresses) {
        try {
            $x = Read-EcByte $Inst ([byte]$a)
            Out-Line ("{0} = {1} ({2})  OK={3}" -f $x.Address, $x.Value, $x.Decimal, $x.OkFlag)
        } catch {
            Out-Line ("0x{0:X2} = READ_ERROR: {1}" -f $a, $_.Exception.Message)
        }
    }

    # Tablas candidatas de curva G2, solo lectura.
    $ranges = @(
        @{Name="CPU_TEMP_CANDIDATE"; Start=0x6A; End=0x6F},
        @{Name="CPU_FAN_CANDIDATE";  Start=0x73; End=0x78},
        @{Name="GPU_TEMP_CANDIDATE"; Start=0x82; End=0x87},
        @{Name="GPU_FAN_CANDIDATE";  Start=0x8B; End=0x90}
    )

    foreach ($range in $ranges) {
        $vals = @()
        for ($a = $range.Start; $a -le $range.End; $a++) {
            try {
                $x = Read-EcByte $Inst ([byte]$a)
                $vals += ("{0}:{1}" -f $x.Address, $x.Value)
            } catch {
                $vals += ("0x{0:X2}:ERR" -f $a)
            }
        }
        Out-Line ("{0} = {1}" -f $range.Name, ($vals -join " "))
    }
}

$script:Lines = New-Object System.Collections.Generic.List[string]

Out-Line "MSI ECO FAN DIAGNOSTIC - READ ONLY"
Out-Line "=================================="
Out-Line "Este script NO modifica perfiles, ventiladores, BIOS ni EC."
Out-Line

# Identidad básica
try {
    $cs = Get-CimInstance Win32_ComputerSystem
    Out-Line ("Manufacturer: {0}" -f $cs.Manufacturer)
    Out-Line ("Model:        {0}" -f $cs.Model)
} catch {
    Out-Line ("MODEL_ERROR: {0}" -f $_.Exception.Message)
}

try {
    $bios = Get-CimInstance Win32_BIOS
    Out-Line ("BIOS:         {0}" -f $bios.SMBIOSBIOSVersion)
    Out-Line ("BIOS Date:    {0}" -f $bios.ReleaseDate)
} catch {
    Out-Line ("BIOS_ERROR: {0}" -f $_.Exception.Message)
}

# MSI Center instalado
try {
    $pkg = Get-AppxPackage -Name "*MSICenter*" -ErrorAction SilentlyContinue |
           Select-Object -First 1 Name, Version, PackageFullName
    if ($pkg) {
        Out-Line ("MSI Center:   {0}  Version={1}" -f $pkg.Name, $pkg.Version)
    } else {
        Out-Line "MSI Center:   AppX package no encontrado con *MSICenter*"
    }
} catch {
    Out-Line ("MSICENTER_ERROR: {0}" -f $_.Exception.Message)
}

# Presencia de interfaz MSI WMI
try {
    $inst = Get-CimInstance -Namespace "root\wmi" -ClassName "MSI_ACPI" | Select-Object -First 1
    if (-not $inst) { throw "MSI_ACPI no devolvió instancia." }
    Out-Line "MSI_ACPI:     FOUND"
    Out-Line "EC firmware / Get_EC:"
    Out-Line (Get-EcFirmwareText $inst)
} catch {
    Out-Line ("MSI_ACPI_ERROR: {0}" -f $_.Exception.Message)
    Out-Line
    Out-Line "No se harán más lecturas EC."
    $desktop = [Environment]::GetFolderPath("Desktop")
    $path = Join-Path $desktop ("MSI_ECO_FAN_DIAG_{0}.txt" -f (Get-Date -Format "yyyyMMdd_HHmmss"))
    $script:Lines | Set-Content -LiteralPath $path -Encoding UTF8
    Out-Line ("Resultado guardado en: {0}" -f $path)
    Read-Host "Pulsa ENTER para cerrar"
    exit 1
}

Out-Line
Out-Line "INSTRUCCIONES:"
Out-Line "1) En MSI Center selecciona ECO-Silencioso y espera ~10 segundos."
Read-Host "   Pulsa ENTER para capturar ECO-SILENCIOSO"
Capture-State $inst "ECO-SILENCIOSO"

Out-Line
Out-Line "2) En MSI Center selecciona Equilibrado y espera ~10 segundos."
Read-Host "   Pulsa ENTER para capturar EQUILIBRADO"
Capture-State $inst "EQUILIBRADO"

Out-Line
Out-Line "3) En MSI Center selecciona Rendimiento extremo con ventilador AUTO y espera ~10 segundos."
Read-Host "   Pulsa ENTER para capturar EXTREMO-AUTO"
Capture-State $inst "EXTREMO-AUTO"

Out-Line
Out-Line "4) Si existe la opción, en Rendimiento extremo entra al engranaje y selecciona ventilador AVANZADO."
Out-Line "   NO cambies aún los puntos de la curva. Espera ~10 segundos."
Read-Host "   Pulsa ENTER para capturar EXTREMO-AVANZADO"
Capture-State $inst "EXTREMO-AVANZADO"

# Restauración mediante la propia UI, no por escritura del script.
Out-Line
Out-Line "5) Vuelve manualmente a ECO-Silencioso en MSI Center."
Read-Host "   Cuando ya esté en ECO-Silencioso, pulsa ENTER para la captura final"
Capture-State $inst "ECO-SILENCIOSO-FINAL"

$desktop = [Environment]::GetFolderPath("Desktop")
$outPath = Join-Path $desktop ("MSI_ECO_FAN_DIAG_{0}.txt" -f (Get-Date -Format "yyyyMMdd_HHmmss"))
$script:Lines | Set-Content -LiteralPath $outPath -Encoding UTF8

Out-Line
Out-Line ("PASS: diagnóstico de solo lectura finalizado.")
Out-Line ("Archivo: {0}" -f $outPath)
Out-Line "Envíame ese TXT completo."
Read-Host "Pulsa ENTER para cerrar"
