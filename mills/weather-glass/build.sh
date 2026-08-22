#!/usr/bin/env bash
# Build / run the weather-glass mill (Unix: Linux / macOS / WSL).
# Windows users: use build.ps1 (same commands).
#
#   ./build.sh run 63011   fetch and draw the report for a US ZIP
#   ./build.sh test        the offline suite - no network, no --allow-net
#   ./build.sh build       weave the program into bin/
#   ./build.sh clean       remove bin/
#
# The run target needs --allow-net: four HTTPS GETs, to api.zippopotam.us
# for the ZIP and to api.weather.gov for the forecast. Both get a
# User-Agent, because the weather service answers 403 without one.
#
# The test target needs NOTHING. Everything between a raw response and a
# finished row is pure, so the captured responses in files/ go through
# the core and the result is compared line by line against
# files/expected.out. That is the whole reason for the core/shell split.
set -euo pipefail
cd "$(dirname "$0")"
. ../../scripts/shoddy-mill-common.sh

case "${1:-run}" in
    run)
        ensure_mill
        "$MILL" run --allow-net weather-glass.shoddy "${2:-63011}"
        ;;
    test)
        ensure_mill
        "$MILL" run test.shoddy < /dev/null
        ;;
    build)
        weave_and_move weather-glass.shoddy
        echo "woven into bin/ - run with: dotnet bin/weather-glass.dll 63011 (needs SHODDY_ALLOW_NET=1)"
        ;;
    clean)
        rm -rf bin
        ;;
    *)
        echo "usage: ./build.sh [run ZIP|test|build|clean]" >&2
        exit 2
        ;;
esac
