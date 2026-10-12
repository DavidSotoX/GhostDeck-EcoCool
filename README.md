# GhostDeck EcoCool Focus 1.39.2

**Edición modificada de [GhostDeck](https://github.com/wygodad/ghostdeck).**

**Proyecto original:** GhostDeck, por Dawid Wygoda / FENIX INSPIRE.  
**Modificaciones y adaptación EcoCool:** David Alberto Soto Ramón.  
**Licencia:** GNU GPL v3 o posterior, conforme al proyecto original. Véase [LICENSE](LICENSE).  
**Atribuciones adicionales:** [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), [LICENSE-ASSETS.md](LICENSE-ASSETS.md).

Esta edición adapta la interfaz Focus y Mini, incorpora supervisión térmica, diagnósticos, opciones de HDR y Turbo CPU, y endurece los mecanismos de validación y recuperación del EC.

## Estado de seguridad

- **Las escrituras manuales al EC permanecen desautorizadas** mientras no se demuestre la compatibilidad exacta del equipo, la BIOS y el firmware EC.
- El programa **no está certificado como controlador manual de ventiladores** en MSI Sword 15 A12UE con BIOS `E1583IMS.110` y EC `1583EMS1.110`.
- Una lectura correcta de una tabla o una variación de RPM **no prueba** que el EC ejecute una curva personalizada.
- No reemplaza el control térmico de fábrica ni MSI Center. No cambies firmware ni desactives protecciones para utilizarlo.
- Compilación y simulaciones verificadas por el operador: 84 comprobaciones estáticas y 130 pruebas de simulación, **no** prueba física de escritura de ventiladores.

## Compilación

Requisitos: Windows x64, .NET SDK **8.0.425**, Python 3.12 y acceso para restaurar paquetes NuGet. Ejecutar desde la raíz del proyecto:

```powershell
.\BUILD_ECOCOOL.cmd
```

El ejecutable resultante se genera en `dist/win-x64/GhostDeckEcoCool.exe`. El artefacto local **no está firmado con Authenticode**. No se publica aquí un instalador ni un ejecutable precompilado. Consulta `d/BUILD.md` si está disponible y `BUILD_ECOCOOL.ps1` para conocer el proceso.

## Procedencia y cambios

Esta es una **obra modificada**, no una versión oficial ni un lanzamiento respaldado por los autores originales. Se conserva la licencia GPL y los avisos de terceros. Las modificaciones comprenden la edición EcoCool Focus/Mini, la adaptación y presentación de modos OEM, protecciones de hardware, validaciones de curvas, diagnósticos, restauración y pruebas de regresión, además de opciones de HDR y Turbo CPU. Véase [CHANGELOG.md](CHANGELOG.md) y [MODIFICACIONES.md](MODIFICACIONES.md).

**MSI** y los nombres de sus productos pertenecen a sus respectivos titulares. Este proyecto no está afiliado a MSI.

## Publicación

Este repositorio contiene código fuente y pruebas. La publicación automática de releases firmadas del proyecto original se ha deshabilitado: depende de identidades y credenciales de terceros, y no debe reutilizarse para esta edición.
