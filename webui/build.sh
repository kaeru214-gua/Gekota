#!/usr/bin/env bash
set -ex
SCRIPT_DIR="$( cd -- "$( dirname -- "${BASH_SOURCE[0]:-$0}"; )" &> /dev/null && pwd 2> /dev/null; )";
pushd "$SCRIPT_DIR/source"
export BASE_URL="./"
export VITE_EMBEDDED_BUILD="true"
npm ci && npx vite build
rm -R ../../Gekota.Web/embeded/ui 2>/dev/null || true
cp -R dist ../../Gekota.Web/embeded/ui
popd
