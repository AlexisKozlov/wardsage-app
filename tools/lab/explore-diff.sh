#!/usr/bin/env bash
# The explorer's findings (scenario explore; the test app's src/Explorer.cs uploads explore-<run>.json through lab_put_file) summed up over
# the last runs, and the corrections they suggest for the UI graph (tools/ui-graph.py -> src/UiGraphData.cs) and the screen map
# (tools/screen-map.py -> src/ScreenMapData.cs). STAGING only; reads lab_files, writes nothing to the database.
#   explore-diff.sh [device] [--runs N]     (default: every device, the last 5 explore files)
#   explore-diff.sh --files a.json ...       (local result files instead: the game sim's, tests)
#   -> $LAB_DIR/explore-suggest-<UTC+3 datetime>.md (printed path), a summary on stdout
# A finding seen in every run that reached the form is a correction to make; one seen in some runs only is a state of the game (listed apart).
set -euo pipefail
. "$(dirname "$0")/lab-common.sh"
command -v jq >/dev/null || { echo "explore-diff: jq is needed" >&2; exit 2; }

dev=""; runs=5; files=()
while [ $# -gt 0 ]; do
  case "$1" in
    --files) shift; files=("$@"); break ;;
    --runs) runs=${2:-5}; shift 2 ;;
    -h|--help) sed -n 2,7p "$0"; exit 0 ;;
    *) dev=$1; shift ;;
  esac
done
[[ "$runs" =~ ^[0-9]{1,3}$ ]] && [ "$runs" -gt 0 ] || { echo "bad --runs" >&2; exit 2; }
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
n=0
if [ ${#files[@]} -gt 0 ]; then
  for f in "${files[@]}"; do jq -e . "$f" >/dev/null 2>&1 && { cp "$f" "$W/r$n.json"; n=$((n + 1)); }; done
else
did=""; [ -n "$dev" ] && did=$(dev_id "$dev")
ids=$(q -v d="$did" -v n="$runs" <<< "
  select f.id from public.lab_files f
  where f.name ~ '^explore-[0-9a-f]{8}\.json$' and (:'d' = '' or f.device_id = :'d'::uuid)
  order by f.created_at desc limit :'n'::int")
[ -n "$ids" ] || { echo "explore-diff: no explore files yet (lab-request.sh <device> --only explore)"; exit 0; }
for id in $ids; do
  q -v i="$id" <<< "select convert_from(data, 'UTF8') from public.lab_files where id = :'i'::uuid" > "$W/r$n.json"
  jq -e . "$W/r$n.json" >/dev/null 2>&1 && n=$((n + 1)) || rm -f "$W/r$n.json"
done
fi
[ "$n" -gt 0 ] || { echo "explore-diff: no readable explore file" >&2; exit 1; }

# all runs as one array: [{run, game, graph, state, visited, forms:[...], mismatches:[...]}]
jq -s '.' "$W"/r*.json > "$W/all.json"

fname="explore-suggest-$(TZ=Etc/GMT-3 date '+%Y%m%d-%H%M').md"; out="$LAB_DIR/$fname"
jq -r --argjson n "$n" '
  def visits($f): [ .[] | select(any(.forms[]; .form == $f)) ] | length;
  . as $runs
  | [ .[] | .mismatches[] ] as $all
  | ($all | group_by([.form, .kind, .widget, (.what | sub("live [-0-9]+,[-0-9]+ [0-9]+x[0-9]+, "; "") | sub(" in [0-9]+ s"; ""))])
      | map({form: .[0].form, kind: .[0].kind, widget: .[0].widget, what: .[0].what, seen: length})) as $g
  | "# Explorer findings: \($n) run(s)",
    "",
    ($runs | map("- run \(.run[0:8]): game \(.game) (graph \(.graph)), \(.state)\(if .why != "" then " (" + .why + ")" else "" end), \(.visited) forms, \(.ok) ok, \(.mismatches | length) mismatches, \(.secs) s") | .[]),
    "",
    "## Suggested corrections (seen in every run that reached the form)",
    "",
    ( $g | map(. as $m | select($m.seen >= ($runs | visits($m.form))))
      | map(. as $m |
          if .kind == "widget" then "- UI graph: \(.form) [\(.widget)] is not shown live - drop its shown flag (tools/ui-graph.py: an inactive parent in the prefab?) or read it by another field"
          elif .kind == "rect" then "- UI graph rect: \(.form) [\(.widget)]: \(.what) - the prefab rect misses a parent offset / layout (tools/ui-graph.py)"
          elif .kind == "reach" and (.what | test(" opened ")) then "- UI graph effect: \(.form) is not what the press opens: \(.what) - fix the widget'"'"'s o: target"
          elif .kind == "reach" then "- UI graph effect: \(.form): \(.what) - the press opens nothing in this state (a condition in the Lua handler, or not the right widget)"
          elif .kind == "close" then "- screen map exit: \(.form): \(.what) - its class / exit rect (tools/screen-map.py)\(if ($runs | any(.forms[]; .form == $m.form and .close == "escape")) then "; Escape closed it: class E" else "" end)"
          elif .kind == "tab" then "- UI graph tab: \(.form) [\(.widget)]: \(.what) - the handler is not a tab switch (t:) but an open / close"
          else "- \(.form) \(.kind): \(.what)" end)
      | if length == 0 then ["(none)"] else . end | .[] ),
    "",
    "## Seen in some runs only (a state of the game, not a correction yet)",
    "",
    ( $g | map(. as $m | select($m.seen < ($runs | visits($m.form))) | "- \(.form) \(.kind) (\(.seen)/\($runs | visits($m.form)) runs): \(.what)")
      | if length == 0 then ["(none)"] else . end | .[] ),
    "",
    "## Ways back that failed",
    "",
    ( [ $runs[] | .forms[] | select(.close != "ok") | "\(.form): \(.close)" ] | unique | if length == 0 then ["(none)"] else . end | .[] )
' "$W/all.json" > "$out"

sum=$(jq -r '[.[] | .mismatches | length] | add' "$W/all.json")
echo "explore-diff: $n run(s), $sum mismatches in all; suggestions: $out"
grep -c '^- UI graph\|^- screen map' "$out" | sed 's/^/explore-diff: corrections suggested: /'
