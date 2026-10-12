# Compilación canónica de EcoCool 1.39.2

La fuente canónica es la raíz que contiene `GhostDeck.csproj`, `Core`, `UI`, `Forms` y `TrayContext.cs`. `bin`, `obj` y `dist` son derivados. No se encontró `.git` ni `CONTROL_TOTAL_MASTER.txt` en el proyecto.

Requisitos: Windows x64, SDK .NET **8.0.425** seleccionado por `global.json`, Python 3 y acceso a NuGet para restauración y auditoría de dependencias. Compilar y ejecutar los harnesses no necesita MSI ni permisos de escritura EC.

```powershell
powershell -NoProfile -File .\BUILD_ECOCOOL.ps1
```

El script verifica SDK, fuente, idiomas y comportamiento simulado; restaura `win-x64`, publica con advertencias como errores y promociona staging únicamente al completar la publicación. Un fallo de compilación no borra el `dist/win-x64` anterior. La distribución sustituida queda en `releases/previous-dist.zip`; un intercambio interrumpido conserva `dist/win-x64.previous` para recuperación explícita.

El resultado predeterminado en `dist/win-x64` requiere .NET 8 Desktop Runtime x64. Para una distribución con runtime:

```powershell
powershell -NoProfile -File .\BUILD_ECOCOOL.ps1 -SelfContained
```

Debe restaurar el mismo modo SelfContained que publicará. Los paquetes runtime pueden requerir descarga. No se afirma que un modo no ejecutado haya sido validado.

Pruebas adicionales, sin hardware:

```powershell
dotnet run --project .\Tests\SafetyHarness.csproj -c Release
dotnet run --project .\Tests\Ipc\IpcHarness.csproj -c Release
dotnet run --project .\Tests\Ipc\IpcHarness.csproj -c Release -- --render reports/FOCUS_UI_1392.png
```

El harness de seguridad enlaza el código real de transacciones, controlador, validación, barrido y archivos seguros con un EC exclusivamente en memoria. El método real `ClearEcoCoolState` se extrae de la fuente canónica para probar el orden de commit sin tocar los ajustes del usuario. IPC usa un pipe aleatorio, nunca la instancia real. El renderizado usa delegates inertes, una ventana invisible y no ejecuta `TrayContext`.

El ejecutable normal solicita administrador según su manifiesto. No se instaló, ejecutó la UI normal ni modificó autostart en esta auditoría. `BUILD PASS` significa construcción de software, no autorización EC ni validación térmica FINAL. Esta edición local no lleva firma Authenticode; no se usan las credenciales ni los workflows de firma upstream.

Rollback íntegro: extraer `releases/baseline-1.39.1.zip` en una carpeta nueva y comprobar `baseline-1.39.1.manifest.json`. No sobrescribir ajustes de usuario ni sustituir un binario en ejecución. Los journals pertenecen al equipo y no se importan desde ZIPs históricos.
