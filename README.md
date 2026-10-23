<p align="center">
  <img src="kaku_logo.png" alt="Kaku" width="280">
</p>

# Kaku（書く）

**Kaku** es un editor de libros EPUB para Windows pensado para **revisar y corregir traducciones** directamente sobre el EPUB final. Se usa como un editor de texto avanzado: abres el libro, buscas la frase que te chirrió en el lector electrónico, la corriges, añades una nota si hace falta y guardas. Sin depender de ningún proceso de traducción externo.

Nació como apoyo para revisar novelas japonesas traducidas al español, pero funciona con cualquier EPUB 2 o 3.

> Interfaz en español · Windows 10/11 · .NET 10 · un único `.exe`

---

## Índice

- [Descargar](#descargar)
- [Características](#características)
- [Requisitos](#requisitos)
- [Compilar el ejecutable](#compilar-el-ejecutable)
- [Configuración y archivos de datos](#configuración-y-archivos-de-datos)
- [Atajos de teclado](#atajos-de-teclado)
- [Estructura del proyecto](#estructura-del-proyecto)
- [Pruebas](#pruebas)
- [Licencia](#licencia)
- [Licencias de terceros](#licencias-de-terceros)

---

## Descargar

Descarga **`Kaku.exe`** de la [última versión](https://github.com/IberiaIberico/kaku/releases/latest) y ejecútalo: no hace falta instalar nada (ver [requisitos](#para-usar-kaku)). Como el ejecutable no está firmado, Windows SmartScreen puede avisar la primera vez: «Más información → Ejecutar de todas formas».

Para compilarlo tú mismo, ver [Compilar el ejecutable](#compilar-el-ejecutable).

---

## Características

### Edición
- **Dos modos por capítulo**, alternables con `Ctrl+E` sin perder la posición:
  - **Modo texto**: prosa limpia, un párrafo por línea, sin etiquetas. Negrita, cursiva, furigana (ruby) y enlaces se muestran como formato; imágenes, saltos de línea y llamadas a notas son "fichas" protegidas que no se pueden romper por accidente.
  - **Modo código**: el XHTML con resaltado de sintaxis. Si el XML queda mal formado aparece una barra roja con «Ir al error» y no se puede volver al modo texto hasta corregirlo.
- `Intro` divide el párrafo, `Retroceso`/`Supr` en los bordes los une, pegar texto crea un párrafo por línea, `Ctrl+B` / `Ctrl+I` para negrita y cursiva.
- Los cambios tocan **solo los elementos editados**: el resto del archivo queda idéntico byte a byte.
- **Deshacer/rehacer** por capítulo (`Ctrl+Z` / `Ctrl+Y`), común a los dos modos.
- **Vista previa en vivo** (WebView2/Edge): el capítulo se ve como en un lector, sigue al cursor, y al hacer clic en un párrafo de la vista previa el editor salta a él.
- **Tema claro u oscuro** (`Ver → Tema`), o el mismo que Windows. En el oscuro, la vista previa muestra el libro en modo noche.

### Navegación
- **Navegador de capítulos en árbol**: los archivos que continúan un capítulo se agrupan bajo él; muestra posición (%), palabras, notas, marcadores y cambios sin guardar.
- **Buscar pasaje** (`Ctrl+Mayús+P`): pegas unas palabras de lo que leíste en el lector y Kaku encuentra el sitio, aunque falten acentos, cambien las comillas o falle alguna palabra.
- **Ir a** (`Ctrl+G`): `45%`, `45,8%`, `w45210` (número de palabra), `7` (capítulo), `7:120` (capítulo y párrafo) o parte del título.
- **Paleta de comandos** (`Ctrl+P`): capítulos, secciones, archivos y comandos.
- **Marcadores** (`Ctrl+F2`, `F2`) y **atrás/adelante** (`Alt+←` / `Alt+→`).
- Posición en la barra de estado: capítulo, párrafo, número de palabra y porcentaje.

### Buscar y reemplazar
- En el capítulo o en todo el libro (`Ctrl+F`, `Ctrl+Mayús+F`, `Ctrl+H`).
- Mayúsculas, palabra completa, **ignorar acentos**, expresiones regulares (`$1`, `${nombre}` en el reemplazo).
- Busca en el **texto** (lo que se lee) o en el **código** (XHTML y CSS).
- **Vista previa de cada reemplazo** antes de aplicarlo, casillas para excluir resultados y «Deshacer reemplazo».
- Búsquedas guardadas con los problemas típicos de traducción (japonés sin traducir, comillas, guiones de diálogo…).

### Estructura del libro (menú **Libro**)
- Añadir páginas (en blanco, *Nota del traductor*, *Personajes*), importar TXT/Markdown/HTML como página, duplicar, renombrar, eliminar.
- **Reordenar** capítulos arrastrando o con `Alt+↑/↓` (un capítulo se mueve junto con sus partes).
- **Dividir** un capítulo en el cursor y **unir** con el siguiente; ids y enlaces se actualizan solos.
- **Galería de imágenes**: añadir, reemplazar, eliminar, usar como portada, insertar en el texto, ver dónde se usa cada una.
- **Editor del índice** (nav.xhtml y toc.ncx): editar, reordenar, cambiar niveles, generarlo desde los encabezados.
- **Metadatos y portada**: título, autores, idioma, editorial, serie, descripción.
- Renombrar cualquier archivo actualiza **todos los enlaces**, el manifiesto y el índice.
- Deshacer de estructura (menú Libro, o `Ctrl+Z` con el foco en el panel de capítulos).

### Notas dentro del libro
- **Insertar nota** (`Ctrl+Alt+N`) detrás de la palabra del cursor; texto con `**negrita**` y `*cursiva*`.
- Notas EPUB 3 estándar (emergentes en Kobo y en la mayoría de lectores) con **enlace de vuelta** y **numeración automática**.
- Colocación: una página «Notas» al final del texto, o al final de cada capítulo. Llamadas `1`, `[1]` o `*`.
- **Panel de notas** (`Ctrl+Mayús+N`) para filtrar, editar, eliminar y saltar; reutilizar una nota para el mismo término; convertir notas en otros formatos (de la editorial) al de Kaku.

### Revisión (panel **Problemas**, `Ctrl+Mayús+M`)
- Texto japonés sin traducir, restos del modelo de traducción (`<|…|>`), palabras repetidas, `¿?`/`¡!` sin pareja, comillas distintas de las del resto del libro, guion en lugar de raya de diálogo, `...` en lugar de `…`, espacios antes o sin espacio después de la puntuación, espacios japoneses y párrafos vacíos.
- **Correcciones automáticas**: una, las seleccionadas o todas las de un tipo (se deshacen con `Ctrl+Z`).
- **Ortografía en español** (Hunspell es_ES): subrayado rojo al escribir, sugerencias con clic derecho, diccionario personal. Ignora los nombres propios del libro, el japonés romanizado (Kōtō) y términos japoneses habituales (kun, sensei, sake…).
- **Validación del EPUB**: enlaces rotos, archivos que faltan o sobran, metadatos obligatorios, ids repetidos, tipos de archivo, idioma del libro.
- **epubcheck** opcional, el validador oficial del W3C (requiere Java, ver abajo).

### Seguridad de los datos
- Guardado **atómico y verificado**; antes de cada guardado se copia la versión anterior a la carpeta de copias de seguridad (se conservan las 10 últimas).
- **Recuperación tras un cierre inesperado**: cada 20 segundos se guarda una copia de los cambios sin guardar y se ofrece recuperarlos al volver a abrir.
- Aviso si el archivo cambió en el disco desde que lo abriste.

---

## Requisitos

### Para usar Kaku
| Requisito | Notas |
|---|---|
| **Windows 10 u 11** (64 bits) | |
| **Microsoft Edge WebView2 Runtime** | Viene instalado en Windows 11 y en Windows 10 actualizados. Si falta, la vista previa muestra un aviso: descárgalo de <https://developer.microsoft.com/microsoft-edge/webview2/>. |
| *Opcional:* **Java 11 o superior** + **epubcheck** | Solo para «Libro → Validar con epubcheck». Ver [epubcheck](#epubcheck-opcional). |

No hace falta instalar .NET: el `.exe` publicado lo lleva incluido.

### Para compilar
| Requisito | Versión |
|---|---|
| **.NET SDK** | 10.0 o superior — <https://dotnet.microsoft.com/download> |
| Conexión a Internet | La primera compilación descarga los paquetes NuGet |
| *Opcional:* Visual Studio 2022 (17.14+) o JetBrains Rider | Para abrir `Kaku.slnx`; no es necesario, basta con la línea de comandos |

Paquetes NuGet que se usan (se descargan solos):

| Paquete | Uso |
|---|---|
| [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) | Editor de texto |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | Vista previa |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | Modelos de vista |
| [WeCantSpell.Hunspell](https://github.com/aarondandy/WeCantSpell.Hunspell) | Corrector ortográfico |
| xunit | Pruebas |

---

## Compilar el ejecutable

```powershell
git clone https://github.com/IberiaIberico/kaku.git
cd kaku

# compilar y ejecutar en modo desarrollo
dotnet build
dotnet run --project src/Kaku.App

# crear el ejecutable único (autocontenido, win-x64)
dotnet publish src/Kaku.App -c Release -o publish
```

El resultado es **`publish\Kaku.exe`** (unos 70 MB): un solo archivo que incluye .NET, el diccionario y los recursos. Se puede copiar a cualquier carpeta y ejecutar sin instalar nada.

Abrir un libro directamente:

```powershell
.\publish\Kaku.exe "C:\Libros\mi libro.epub"
```

También se puede arrastrar un `.epub` a la ventana o usar `Archivo → Abrir` (`Ctrl+O`).

> Las opciones de publicación (archivo único, autocontenido, `win-x64`, compresión) están en `src/Kaku.App/Kaku.App.csproj`, y `Directory.Build.props` evita que se generen `.pdb` en Release.

---

## Configuración y archivos de datos

Kaku no necesita configuración inicial. Guarda sus datos en estas carpetas:

| Ruta | Contenido |
|---|---|
| `%APPDATA%\Kaku\settings.json` | Libros recientes, tamaño de la ventana y paneles, búsquedas guardadas, ortografía activada, tema, ruta de epubcheck |
| `%APPDATA%\Kaku\bookmarks.json` | Marcadores de cada libro |
| `%APPDATA%\Kaku\diccionario-personal.txt` | Diccionario personal (una palabra por línea; también desde `Editar → Diccionario personal…`) |
| `%APPDATA%\Kaku\error.log` | Registro de errores inesperados |
| `%LOCALAPPDATA%\Kaku\Backups\<libro>\` | Copia de la versión anterior en cada guardado (las 10 últimas). `Archivo → Abrir carpeta de copias de seguridad` |
| `%LOCALAPPDATA%\Kaku\Recovery\` | Cambios sin guardar para recuperar tras un cierre inesperado |
| `%LOCALAPPDATA%\Kaku\WebView2\` | Caché de la vista previa |

Para empezar de cero basta con borrar estas carpetas con Kaku cerrado.

Ajustes que se guardan **dentro del propio EPUB** (viajan con el libro): colocación y estilo de las notas (`<meta name="kaku:notes-…">` en el OPF) y la hoja de estilos de notas `kaku-notas.css`.

### epubcheck (opcional)

El panel de problemas ya hace las comprobaciones principales del EPUB. Para la validación oficial:

1. Instala **Java 11 o superior**, por ejemplo [Eclipse Temurin](https://adoptium.net). Kaku lo busca en `JAVA_HOME` y en el `PATH`.
2. Descarga **epubcheck** desde <https://github.com/w3c/epubcheck/releases> y descomprime el zip.
3. En Kaku: `Libro → Validar con epubcheck…`. La primera vez te pide que elijas `epubcheck.jar`; la ruta se recuerda.

Kaku valida una copia del estado actual del libro (no hace falta guardar antes) y muestra los mensajes en el panel de problemas.

---

## Atajos de teclado

| Atajo | Acción |
|---|---|
| `Ctrl+O` / `Ctrl+S` / `Ctrl+Mayús+S` | Abrir / Guardar / Guardar como |
| `Ctrl+Z` / `Ctrl+Y` | Deshacer / Rehacer (en el panel de capítulos: cambios de estructura) |
| `Ctrl+E` | Cambiar entre modo texto y modo código |
| `Ctrl+B` / `Ctrl+I` | Negrita / Cursiva |
| `Ctrl+P` | Paleta de comandos |
| `Ctrl+G` | Ir a posición |
| `Ctrl+Mayús+P` | Buscar pasaje (pegar del lector) |
| `Ctrl+F` / `Ctrl+Mayús+F` / `Ctrl+H` | Buscar en el capítulo / en el libro / Reemplazar |
| `F3` / `Mayús+F3` | Resultado siguiente / anterior |
| `Ctrl+F2` / `F2` / `Mayús+F2` | Poner o quitar marcador / siguiente / anterior |
| `Alt+←` / `Alt+→` | Atrás / Adelante |
| `Ctrl+AvPág` / `Ctrl+RePág` | Capítulo siguiente / anterior |
| `Alt+↑` / `Alt+↓` | Mover capítulo (en el panel de capítulos) |
| `Ctrl+Alt+Intro` | Dividir el capítulo en el cursor |
| `Ctrl+Alt+N` | Insertar nota |
| `Ctrl+Alt+I` | Insertar imagen |
| `Ctrl+Alt+T` | Editor del índice |
| `Ctrl+J` | Mostrar u ocultar el panel inferior |
| `Ctrl+Mayús+N` / `Ctrl+Mayús+M` | Panel de notas / Panel de problemas |
| `Ctrl+Alt+B` / `Ctrl+Alt+P` | Mostrar u ocultar panel lateral / vista previa |
| `Ctrl+W` | Cerrar pestaña |

Los atajos también funcionan con el foco en la vista previa.

---

## Estructura del proyecto

```
kaku/
├── Kaku.slnx                 Solución
├── Directory.Build.props     Opciones comunes de compilación
├── SPEC.md                   Especificación y registro de decisiones
├── kaku_logo.png / .svg      Logo
├── src/
│   ├── Kaku.Core/            Lógica sin interfaz (net10.0)
│   │   ├── Epub/             Lectura y escritura del EPUB, OPF, índice, XML
│   │   ├── Text/             Modelo de texto: bloques, palabras, normalización
│   │   ├── Editing/          Edición segura del XHTML, historial, copias, recuperación
│   │   ├── Search/           Buscar/reemplazar y "buscar pasaje"
│   │   ├── Navigation/       Ir a, historial atrás/adelante
│   │   ├── Structure/        Páginas, enlaces, índice, metadatos, notas, revisión del paquete
│   │   ├── Notes/            Índice de notas y formato del texto de las notas
│   │   ├── Checks/           Comprobaciones de texto, ortografía, validación EPUB
│   │   └── Preview/          XHTML para la vista previa
│   └── Kaku.App/             Aplicación WPF (net10.0-windows)
│       ├── Editor/           Vista de capítulo, coloreado, subrayados
│       ├── Views/            Paneles y diálogos
│       ├── ViewModels/       Filas del navegador, resultados…
│       ├── Services/         Ajustes, marcadores, ortografía
│       ├── Localization/     Textos de la interfaz (Strings.resx)
│       ├── Resources/        Script de la vista previa, diccionario es_ES
│       └── Assets/           Icono y logo
└── tests/
    └── Kaku.Core.Tests/      Pruebas (xUnit)
```

Todos los textos de la interfaz están en `src/Kaku.App/Localization/Strings.resx`; el código y los comentarios están en inglés.

---

## Pruebas

```powershell
dotnet test
```

La mayoría de las pruebas usan libros sintéticos creados en memoria. Algunas, además, abren, editan y guardan **libros reales** si encuentran una carpeta `kobo_downloads` (y `out`) en algún directorio superior al del proyecto; si no existen, esas comprobaciones simplemente no tienen libros que revisar. Los libros no forman parte del repositorio.

---

## Licencia

Kaku se distribuye bajo la **licencia MIT**: puedes usarlo, copiarlo, modificarlo y redistribuirlo libremente, también con fines comerciales, siempre que conserves el aviso de copyright. El texto completo está en [`LICENSE`](LICENSE).

## Licencias de terceros

- **Diccionario español (es_ES)** del proyecto [LibreOffice dictionaries](https://github.com/LibreOffice/dictionaries/tree/master/es), de Santiago Bosio y colaboradores, con triple licencia GNU GPL 3+, GNU LGPL 3+ o MPL 1.1+. Se incluye en `src/Kaku.App/Resources/Dictionaries/` junto con su `LICENSE.md` y `README_hunspell_es.txt`.
- **AvalonEdit** — MIT. **CommunityToolkit.Mvvm** — MIT. **WeCantSpell.Hunspell** — MPL 1.1 / GPL 2 / LGPL 2.1. **Microsoft.Web.WebView2** — licencia de Microsoft (BSD para el SDK).

Kaku no incluye ni gestiona DRM: está pensado para libros sin protección.
