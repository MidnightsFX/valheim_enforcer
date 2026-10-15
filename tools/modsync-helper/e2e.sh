#!/usr/bin/env bash
# End to end check of a built mod sync helper, on Linux or macOS:
#
#   bash e2e.sh <helper binary>
#
# A stand-in "game" (a shell script) is started the way a BepInEx launch script starts Valheim - Doorstop settings
# in the environment, plus a stale DOORSTOP_INITIALIZED that must not survive - from a working directory with a
# space in it. It writes a plan, starts the helper and exits. The helper has to wait for it, apply the plan, and
# start it again: same arguments with the join swapped, same working directory, Doorstop's settings kept and its
# marker gone. Then a game that never exits must leave everything untouched.
#
# The relaunch is the one each platform uses: Linux reads the game's launch from /proc, macOS gets the command line
# and environment in the plan. VE_E2E_MODE=explicit forces the macOS form on Linux, so both can be run on one box.
set -u

if [ $# -ne 1 ] || [ ! -f "$1" ]; then
    echo "usage: $0 <helper binary>" >&2
    exit 2
fi

mode="${VE_E2E_MODE:-}"
if [ -z "$mode" ]; then
    if [ "$(uname -s)" = Linux ]; then mode=proc; else mode=explicit; fi
fi

base="$(mktemp -d)"
R="$base/ve modsync e2e"
mkdir -p "$R/plugins/Extra" "$R/plugins/Own-Enf" "$R/staged/Own-Enf" "$R/staged/Own-Enf.config/sub" "$R/config" "$R/bin" "$R/game dir"
echo extra > "$R/plugins/Extra/Extra 50%.dll"
echo old > "$R/plugins/Own-Enf/Enf.dll"
echo new > "$R/staged/Own-Enf/Enf.dll"
echo fresh > "$R/staged/Own-Enf.config/sub/a.cfg"
cp "$1" "$R/bin/ve-modsync"
chmod 755 "$R/bin/ve-modsync"

cat > "$R/fakegame.sh" <<'GAME'
#!/bin/sh
R="$VE_E2E_ROOT"
case " $* " in
  *" +connect 1.2.3.4:2456"*)
    printf '%s\n' "$@" > "$R/relaunched-args.txt"
    env | grep -E '^(DOORSTOP_|VE_E2E_FLAG)' | sort > "$R/relaunched-env.txt"
    pwd -P > "$R/relaunched-cwd.txt"
    exit 0 ;;
esac
{
  printf 've-modsync\t1\npid\t%s\ntimeout\t30\nlog\t%s\n' "$$" "$R/apply.log"
  printf 'disable\t%s\n' "$R/plugins/Extra/Extra 50%.dll"
  printf 'replace\t%s\t%s\t%s\n' "$R/staged/Own-Enf" "$R/plugins/Own-Enf" "$R/replaced/stamp"
  printf 'copy-new\t%s\t%s\n' "$R/staged/Own-Enf.config/sub/a.cfg" "$R/config/sub/a.cfg"
  printf 'cleanup\t%s\n' "$R/staged"
  if [ "$VE_E2E_MODE" = proc ]; then
    printf 'relaunch\tproc\n'
  else
    printf 'relaunch\texplicit\nexe\t/bin/sh\narg\t%s\n' "$0"
    for a in "$@"; do printf 'arg\t%s\n' "$a"; done
    printf 'cwd\t%s\n' "$PWD"
    # What the mod writes is the whole environment; these are the ones the check reads.
    for k in PATH HOME VE_E2E_ROOT VE_E2E_MODE VE_E2E_FLAG DOORSTOP_ENABLED DOORSTOP_TARGET_ASSEMBLY DOORSTOP_INITIALIZED; do
      eval "v=\${$k-}"
      printf 'env\t%s\t%s\n' "$k" "$v"
    done
  fi
  printf 'drop-arg-pair\t+connect\nappend-arg\t+connect\nappend-arg\t1.2.3.4:2456\n'
} > "$R/apply.plan"
"$R/bin/ve-modsync" "$R/apply.plan" &
sleep 2
echo exited > "$R/game-exited.txt"
exit 0
GAME

cd "$R/game dir"
env VE_E2E_ROOT="$R" VE_E2E_MODE="$mode" VE_E2E_FLAG=kept \
    DOORSTOP_INITIALIZED=TRUE DOORSTOP_ENABLED=1 DOORSTOP_TARGET_ASSEMBLY="$R/BepInEx.Preloader.dll" \
    /bin/sh "$R/fakegame.sh" --doorstop-x "a b" +connect old.host:1 -console

for _ in $(seq 1 150); do [ -f "$R/relaunched-args.txt" ] && break; sleep 0.1; done
sleep 0.5

echo "mode: $mode"
echo "--- log"; cat "$R/apply.log" 2>/dev/null
echo "--- relaunched args"; cat "$R/relaunched-args.txt" 2>/dev/null
echo "--- relaunched env"; cat "$R/relaunched-env.txt" 2>/dev/null
echo "--- relaunched cwd"; cat "$R/relaunched-cwd.txt" 2>/dev/null
echo "---"

pass=0; fail=0
check() {
    if eval "$1"; then pass=$((pass + 1)); echo "PASS $2"; else fail=$((fail + 1)); echo "FAIL $2"; fi
}
check '[ ! -e "$R/plugins/Extra/Extra 50%.dll" ] && [ "$(cat "$R/plugins/Extra/Extra 50%.dll.old")" = extra ]' "unlisted mod renamed to .old"
check '[ "$(cat "$R/plugins/Own-Enf/Enf.dll")" = new ]' "replaced folder holds the new build"
check '[ "$(cat "$R/replaced/stamp/Own-Enf/Enf.dll")" = old ]' "old folder kept in the backup"
check '[ "$(cat "$R/config/sub/a.cfg")" = fresh ]' "config added"
check '[ ! -e "$R/staged" ] && [ ! -e "$R/apply.plan" ]' "staging and plan removed"
check '[ -f "$R/game-exited.txt" ]' "the game ran to its exit before anything was applied"
check '[ "$(tr "\n" "|" < "$R/relaunched-args.txt")" = "--doorstop-x|a b|-console|+connect|1.2.3.4:2456|" ]' "arguments intact, the old join swapped for the new"
check '[ "$(tr "\n" "|" < "$R/relaunched-env.txt")" = "DOORSTOP_ENABLED=1|DOORSTOP_TARGET_ASSEMBLY=$R/BepInEx.Preloader.dll|VE_E2E_FLAG=kept|" ]' "Doorstop settings kept, DOORSTOP_INITIALIZED stripped"
# Compared with symlinks resolved on both sides. On macOS /var is a link to /private/var, and the relaunched shell
# reports its directory one way or the other depending on whether it was handed a PWD - it is the same directory.
check '[ "$(cat "$R/relaunched-cwd.txt")" = "$(cd "$R/game dir" && pwd -P)" ]' "same working directory"

# A game that never exits: the helper waits on this script, gives up, and changes nothing.
mkdir -p "$R/t2/staged"
echo keep > "$R/t2/keep.dll"
printf 've-modsync\t1\npid\t%s\ntimeout\t2\nlog\t%s\ndisable\t%s\ncleanup\t%s\nrelaunch\tnone\n' "$$" "$R/t2/apply.log" "$R/t2/keep.dll" "$R/t2/staged" > "$R/t2/apply.plan"
"$R/bin/ve-modsync" "$R/t2/apply.plan"
code=$?
check '[ "$code" = 1 ] && [ -f "$R/t2/keep.dll" ] && [ ! -e "$R/t2/staged" ] && grep -q "did not exit within 2 seconds" "$R/t2/apply.log"' "a game that never exits: nothing changed, exit code 1"

# A plan from a newer mod is refused before anything happens, with the reason left beside it.
mkdir -p "$R/t3"
printf 've-modsync\t2\npid\t1\nlog\tx\n' > "$R/t3/apply.plan"
"$R/bin/ve-modsync" "$R/t3/apply.plan"
code=$?
check '[ "$code" = 2 ] && grep -q "not a plan this helper understands" "$R/t3/apply.error.log"' "an unknown plan version is refused with a reason"

echo "$pass passed, $fail failed"
rm -rf "$base"
[ "$fail" = 0 ]
