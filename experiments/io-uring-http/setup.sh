#!/usr/bin/env bash
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
mkdir -p "$HOME/code"

if [[ ! -d "$HOME/code/liburing" ]]; then
    git clone --branch liburing-2.9 --depth 1 https://github.com/axboe/liburing.git "$HOME/code/liburing"
fi
if [[ $(git -C "$HOME/code/liburing" rev-parse HEAD) != 08468cc3830185c75f9e7edefd88aa01e5c2f8ab ]]; then
    echo "Unexpected liburing checkout; use the documented liburing-2.9 revision." >&2
    exit 1
fi
(
    cd "$HOME/code/liburing"
    ./configure --prefix="$HOME/.local"
    make -j4
    make install
)

if [[ ! -d "$HOME/code/wrk2" ]]; then
    git clone https://github.com/giltene/wrk2.git "$HOME/code/wrk2"
    git -C "$HOME/code/wrk2" checkout --detach 44a94c17d8e6a0bac8559b53da76848e430cb7a7
fi
if [[ $(git -C "$HOME/code/wrk2" rev-parse HEAD) != 44a94c17d8e6a0bac8559b53da76848e430cb7a7 ]]; then
    echo "Unexpected wrk2 checkout; use the documented revision." >&2
    exit 1
fi
if git -C "$HOME/code/wrk2" apply --check "$root/wrk2-monotonic.patch"; then
    git -C "$HOME/code/wrk2" apply "$root/wrk2-monotonic.patch"
elif ! git -C "$HOME/code/wrk2" apply --reverse --check "$root/wrk2-monotonic.patch"; then
    echo "wrk2 clock patch conflicts with existing changes." >&2
    exit 1
fi
make -C "$HOME/code/wrk2" -j4
make -C "$root"
