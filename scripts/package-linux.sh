#!/usr/bin/env bash
# ==============================================================================
# Pankin Linux Packaging Script (.deb & .AppImage)
# Ubuntu 24.04 LTS / GNOME (Wayland / X11)
# ==============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

VERSION="0.5.1"
APP_NAME="pankin"
APP_DISPLAY_NAME="Pankin"
DEB_ARCH="amd64"
APPIMAGE_ARCH="x86_64"

ARTIFACTS_DIR="${ROOT_DIR}/artifacts"
PUBLISH_DIR="${ARTIFACTS_DIR}/publish/linux-x64"
DEB_ROOT="${ARTIFACTS_DIR}/deb-build"
APPDIR="${ARTIFACTS_DIR}/AppDir"
TOOLS_DIR="${ROOT_DIR}/.tools"

DEB_FILE="${ARTIFACTS_DIR}/${APP_NAME}_${VERSION}_${DEB_ARCH}.deb"
APPIMAGE_FILE="${ARTIFACTS_DIR}/${APP_DISPLAY_NAME}-${VERSION}-${APPIMAGE_ARCH}.AppImage"
ICON_FILE="${ARTIFACTS_DIR}/pankin.svg"
ICON_PNG_FILE="${ARTIFACTS_DIR}/pankin.png"

echo "========================================================"
echo " Packaging ${APP_DISPLAY_NAME} v${VERSION} for Linux x64"
echo "========================================================"

# 1. Clean previous build artifacts
rm -rf "${PUBLISH_DIR}" "${DEB_ROOT}" "${APPDIR}"
mkdir -p "${ARTIFACTS_DIR}"

# 2. Copy Brand Logo Assets (Option B)
echo "==> [1/4] Copying application icon assets: ${ICON_FILE}, ${ICON_PNG_FILE}"
cp "${ROOT_DIR}/src/Pankin.Desktop/Assets/pankin-logo.svg" "${ICON_FILE}"
cp "${ROOT_DIR}/src/Pankin.Desktop/Assets/pankin-logo.png" "${ICON_PNG_FILE}"

# 3. Publish self-contained .NET 8 binary
echo "==> [2/4] Publishing self-contained .NET 8 release to ${PUBLISH_DIR}..."
dotnet publish "${ROOT_DIR}/src/Pankin.Desktop/Pankin.Desktop.csproj" \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:Version="${VERSION}" \
  -o "${PUBLISH_DIR}"

chmod +x "${PUBLISH_DIR}/Pankin.Desktop"

# 4. Assemble & Build Debian Package (.deb)
echo "==> [3/4] Assembling Debian package structure..."
mkdir -p "${DEB_ROOT}/DEBIAN"
mkdir -p "${DEB_ROOT}/opt/pankin"
mkdir -p "${DEB_ROOT}/usr/bin"
mkdir -p "${DEB_ROOT}/usr/share/applications"
mkdir -p "${DEB_ROOT}/usr/share/icons/hicolor/scalable/apps"
mkdir -p "${DEB_ROOT}/usr/share/icons/hicolor/256x256/apps"
mkdir -p "${DEB_ROOT}/usr/share/pixmaps"

# Debian control file
cat << EOF > "${DEB_ROOT}/DEBIAN/control"
Package: ${APP_NAME}
Version: ${VERSION}
Section: utils
Priority: optional
Architecture: ${DEB_ARCH}
Maintainer: Pankin Project <https://github.com/demgkp1/Pankin>
Depends: libc6, libgcc-s1, libstdc++6, plocate, xclip, wl-clipboard, libnotify-bin, libgdk-pixbuf-2.0-0, librsvg2-common
Description: Native modern desktop productivity platform for Linux
 Pankin is a native, modern desktop productivity platform for Ubuntu/Linux.
 Features lightning-fast launcher, clipboard history, selection super panel,
 AI translation/chat, and directory jumping.
EOF

# Desktop entry
cat << 'EOF' > "${DEB_ROOT}/usr/share/applications/pankin.desktop"
[Desktop Entry]
Type=Application
Name=Pankin
GenericName=Productivity Platform
Comment=Native Modern Desktop Productivity Platform for Linux
Exec=/usr/bin/pankin %u
Icon=pankin
Terminal=false
Categories=Utility;System;
StartupWMClass=pankin
Keywords=launcher;clipboard;productivity;spotlight;
EOF

# Copy binaries, wrapper script, and icons
cp -a "${PUBLISH_DIR}/." "${DEB_ROOT}/opt/pankin/"

cat << 'EOF' > "${DEB_ROOT}/usr/bin/pankin"
#!/bin/bash
exec /opt/pankin/Pankin.Desktop "$@"
EOF
chmod 755 "${DEB_ROOT}/usr/bin/pankin"

cp "${ICON_FILE}" "${DEB_ROOT}/usr/share/icons/hicolor/scalable/apps/pankin.svg"
cp "${ICON_FILE}" "${DEB_ROOT}/usr/share/pixmaps/pankin.svg"
cp "${ICON_PNG_FILE}" "${DEB_ROOT}/usr/share/icons/hicolor/256x256/apps/pankin.png"

chmod -R a+rX "${DEB_ROOT}"

echo "==> Building ${DEB_FILE}..."
dpkg-deb --build --root-owner-group "${DEB_ROOT}" "${DEB_FILE}"
rm -rf "${DEB_ROOT}"

# 5. Assemble & Build AppImage
echo "==> [4/4] Assembling AppDir structure..."
mkdir -p "${APPDIR}/usr/bin"
mkdir -p "${APPDIR}/usr/share/applications"
mkdir -p "${APPDIR}/usr/share/icons/hicolor/scalable/apps"
mkdir -p "${APPDIR}/usr/share/icons/hicolor/256x256/apps"
mkdir -p "${APPDIR}/usr/share/pixmaps"

# AppRun launcher script
cat << 'EOF' > "${APPDIR}/AppRun"
#!/bin/bash
HERE="$(dirname "$(readlink -f "${0}")")"
export PATH="${HERE}/usr/bin:${PATH}"
export LD_LIBRARY_PATH="${HERE}/usr/bin:${LD_LIBRARY_PATH:-}"
exec "${HERE}/usr/bin/Pankin.Desktop" "$@"
EOF
chmod 755 "${APPDIR}/AppRun"

# AppDir desktop entry and icons
cat << 'EOF' > "${APPDIR}/pankin.desktop"
[Desktop Entry]
Type=Application
Name=Pankin
GenericName=Productivity Platform
Comment=Native Modern Desktop Productivity Platform for Linux
Exec=Pankin.Desktop %u
Icon=pankin
Terminal=false
Categories=Utility;
StartupWMClass=pankin
Keywords=launcher;clipboard;productivity;spotlight;
EOF

cp "${APPDIR}/pankin.desktop" "${APPDIR}/usr/share/applications/pankin.desktop"
cp "${ICON_FILE}" "${APPDIR}/pankin.svg"
cp "${ICON_PNG_FILE}" "${APPDIR}/pankin.png"
cp "${ICON_PNG_FILE}" "${APPDIR}/.DirIcon"
cp "${ICON_FILE}" "${APPDIR}/usr/share/icons/hicolor/scalable/apps/pankin.svg"
cp "${ICON_PNG_FILE}" "${APPDIR}/usr/share/icons/hicolor/256x256/apps/pankin.png"
cp "${ICON_FILE}" "${APPDIR}/usr/share/pixmaps/pankin.svg"

# Copy binaries into AppDir/usr/bin
cp -a "${PUBLISH_DIR}/." "${APPDIR}/usr/bin/"

# Ensure proper read and execute permissions for AppDir
chmod -R a+rX "${APPDIR}"

# Locate or download appimagetool
APPIMAGETOOL=""
if command -v appimagetool &>/dev/null; then
  APPIMAGETOOL="$(command -v appimagetool)"
elif [ -x "${TOOLS_DIR}/appimagetool" ]; then
  APPIMAGETOOL="${TOOLS_DIR}/appimagetool"
else
  mkdir -p "${TOOLS_DIR}"
  echo "==> Downloading appimagetool to ${TOOLS_DIR}/appimagetool..."
  curl -fsSL -o "${TOOLS_DIR}/appimagetool" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "${TOOLS_DIR}/appimagetool"
  APPIMAGETOOL="${TOOLS_DIR}/appimagetool"
fi

echo "==> Building ${APPIMAGE_FILE} with appimagetool..."
# Prefer a cached runtime so rebuilding does not depend on a GitHub download.
RUNTIME_ARGS=()
if [ -n "${APPIMAGE_RUNTIME_FILE:-}" ]; then
  if [ ! -f "${APPIMAGE_RUNTIME_FILE}" ]; then
    echo "ERROR: APPIMAGE_RUNTIME_FILE does not exist: ${APPIMAGE_RUNTIME_FILE}" >&2
    exit 1
  fi
  RUNTIME_ARGS=(--runtime-file "${APPIMAGE_RUNTIME_FILE}")
elif [ -f "${TOOLS_DIR}/runtime-x86_64" ]; then
  RUNTIME_ARGS=(--runtime-file "${TOOLS_DIR}/runtime-x86_64")
fi
ARCH=x86_64 "${APPIMAGETOOL}" --appimage-extract-and-run "${RUNTIME_ARGS[@]}" "${APPDIR}" "${APPIMAGE_FILE}"
rm -rf "${APPDIR}"

# Versioned basenames keep the manifest portable and compatible with UpdateService.
CHECKSUMS_FILE="${ARTIFACTS_DIR}/SHA256SUMS.txt"
(
  cd "${ARTIFACTS_DIR}"
  sha256sum "$(basename "${APPIMAGE_FILE}")" "$(basename "${DEB_FILE}")"
) > "${CHECKSUMS_FILE}"

echo ""
echo "========================================================"
echo " Packaging Success!"
echo " Artifacts:"
ls -lh "${DEB_FILE}" "${APPIMAGE_FILE}" "${CHECKSUMS_FILE}" "${ICON_FILE}" "${ICON_PNG_FILE}"
echo "========================================================"
