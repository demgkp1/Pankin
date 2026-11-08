#!/usr/bin/env bash
# ==============================================================================
# Pankin Linux Packaging Script (.deb & .AppImage)
# Ubuntu 24.04 LTS / GNOME (Wayland / X11)
# ==============================================================================

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

VERSION="0.2.0"
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

echo "========================================================"
echo " Packaging ${APP_DISPLAY_NAME} v${VERSION} for Linux x64"
echo "========================================================"

# 1. Clean previous build artifacts
rm -rf "${PUBLISH_DIR}" "${DEB_ROOT}" "${APPDIR}"
mkdir -p "${ARTIFACTS_DIR}"

# 2. Generate Brand SVG Icon
echo "==> [1/4] Generating application icon: ${ICON_FILE}"
cat << 'EOF' > "${ICON_FILE}"
<?xml version="1.0" encoding="UTF-8"?>
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512">
  <defs>
    <linearGradient id="pankinGrad" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" stop-color="#8B8DFF"/>
      <stop offset="100%" stop-color="#5B57E8"/>
    </linearGradient>
    <filter id="pankinShadow" x="-10%" y="-10%" width="120%" height="120%">
      <feDropShadow dx="0" dy="12" stdDeviation="16" flood-color="#000000" flood-opacity="0.25"/>
    </filter>
  </defs>
  <rect x="32" y="32" width="448" height="448" rx="96" ry="96" fill="url(#pankinGrad)" filter="url(#pankinShadow)"/>
  <text x="256" y="348" font-family="-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, 'Noto Sans', sans-serif" font-size="280" font-weight="bold" fill="#FFFFFF" text-anchor="middle">P</text>
</svg>
EOF

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
mkdir -p "${DEB_ROOT}/usr/share/pixmaps"

# Debian control file
cat << EOF > "${DEB_ROOT}/DEBIAN/control"
Package: ${APP_NAME}
Version: ${VERSION}
Section: utils
Priority: optional
Architecture: ${DEB_ARCH}
Maintainer: Pankin Project <https://github.com/Pankin/Pankin>
Depends: libc6, libgcc-s1, libstdc++6, plocate, xclip, wl-clipboard, libnotify-bin
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

chmod -R a+rX "${DEB_ROOT}"

echo "==> Building ${DEB_FILE}..."
dpkg-deb --build --root-owner-group "${DEB_ROOT}" "${DEB_FILE}"
rm -rf "${DEB_ROOT}"

# 5. Assemble & Build AppImage
echo "==> [4/4] Assembling AppDir structure..."
mkdir -p "${APPDIR}/usr/bin"
mkdir -p "${APPDIR}/usr/share/applications"
mkdir -p "${APPDIR}/usr/share/icons/hicolor/scalable/apps"
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
cp "${ICON_FILE}" "${APPDIR}/usr/share/icons/hicolor/scalable/apps/pankin.svg"
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
ARCH=x86_64 "${APPIMAGETOOL}" --appimage-extract-and-run "${APPDIR}" "${APPIMAGE_FILE}"
rm -rf "${APPDIR}"

echo ""
echo "========================================================"
echo " Packaging Success!"
echo " Artifacts:"
ls -lh "${DEB_FILE}" "${APPIMAGE_FILE}" "${ICON_FILE}"
echo "========================================================"
