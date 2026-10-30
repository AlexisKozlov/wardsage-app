#!/usr/bin/env bash
# Report of one test-lab run (staging): a short Russian Telegram message + ~/wardsage/work/lab/report-<UTC+3 datetime>.md
#   lab-report.sh <run id>        (called by lab-loop.sh at the end; may be run again by hand; --no-telegram skips the message) (LAB_NO_TG=1 skips the message)
# Per step ✅/❌ with seconds; the "first read N ms" and "passes:" figures come from the staging app_test_logs
# (the Log.TestQueue lines of the test app, src/TestLog.cs). The token of the alerts bot is never printed.
set -uo pipefail
. "$(dirname "$0")/lab-common.sh"

run=${1:-}; send=1
{ [ "${2:-}" = "--no-telegram" ] || [ -n "${LAB_NO_TG:-}" ]; } && send=0
[[ "$run" =~ ^[0-9a-f-]{36}$ ]] || { echo "usage: lab-report.sh <run id> [--no-telegram]" >&2; exit 2; }

info=$(q -F '|' -v r="$run" <<< "
  select dv.name, r.state, r.build, coalesce(extract(epoch from r.started_at)::bigint, 0), coalesce(r.data->>'finish_ok', '0'), dv.account_id is not null
  from public.lab_runs r join public.lab_devices dv on dv.id = r.device_id where r.id = :'r'::uuid") || exit 1
[ -n "$info" ] || { echo "unknown run" >&2; exit 1; }
IFS='|' read -r devname state build started finish_ok _ <<< "$info"

# logs of the account since the run started (two minutes of slack for the log batching)
logs=$(q -v r="$run" <<< "
  select l.lines from public.app_test_logs l
  join public.lab_devices dv on dv.account_id = l.account_id
  join public.lab_runs r on r.device_id = dv.id
  where r.id = :'r'::uuid and l.at >= r.started_at - interval '2 minutes' order by l.at, l.id")
firstreads=$(grep -oE '(inventory|heroes|foundry) first read [0-9]+ ms' <<< "$logs" | sed -E 's/ first read / /' | awk '{print $1 ": " $2 " ms"}' | awk '!seen[$0]++' | head -6)
passes=$(grep -E 'passes: last' <<< "$logs" | tail -3 | sed -E 's/^[0-9-]+ [0-9:]+  //')

TZ_NOW=$(TZ=Etc/GMT-3 date '+%Y-%m-%d %H:%M')
fname="report-$(TZ=Etc/GMT-3 date '+%Y%m%d-%H%M').md"

names() { case "$1" in
  online) echo "Ноутбук на связи" ;; update_app*) echo "Обновление тестовой сборки" ;; restart_app) echo "Перезапуск приложения" ;;
  app_ready) echo "Приложение готово" ;; equip_city*) echo "Надеть сборку из Города${1#equip_city}" ;;
  equip_hide_worn) echo "Надеть вещь другого героя («Скрыть надетое»)" ;; equip_artifact) echo "Надеть артефакт" ;;
  explore) echo "Обход интерфейса (граф UI)" ;; locate) echo "Найти в игре" ;; locate_recast) echo "Найти в игре: преобразование" ;; locate_art) echo "Найти в игре: продвинуть артефакт" ;; grades) echo "Оценки героев" ;; sell_mark) echo "Чистка склада: выделение" ;; sell_after_restart) echo "Чистка склада после перезапуска" ;; upload_logs) echo "Загрузка логов" ;; screenshot) echo "Скриншот окна" ;; timeout) echo "Время вышло" ;; *) echo "$1" ;; esac; }

lines=""
while IFS='|' read -r name ok secs note warnf infof recid; do
  [ -n "$name" ] || continue
  mark="✅"; [ "$ok" = t ] || mark="❌"; [ "$warnf" = true ] && mark="⚠️"; [ "$infof" = true ] && mark="ℹ️"
  txt="$mark $(names "$name"): ${secs} с"; [ -n "$note" ] && txt="$txt ($note)"
  [ -n "$recid" ] && txt="$txt [запись $recid]"   # the scenario's screen recording (lab-rec.sh)
  lines+="$txt"$'\n'
done < <(q -F '|' -v r="$run" <<< "select s->>'name', ((s->>'ok')::boolean)::text::text, s->>'seconds', s->>'note', coalesce(s->>'warn', ''), coalesce(s->>'info', ''), coalesce(s->>'rec', '') from public.lab_runs r, jsonb_array_elements(r.steps) s where r.id = :'r'::uuid" | sed -E 's/\|true\|/|t|/; s/\|false\|/|f|/')

# the equip plan finished without a single slot / «Заменить» click: the build was already worn, nothing was tested (a warning, not a pass)
nochange=0
if [ "$(q -v r="$run" <<< "select count(*) from public.lab_runs r, jsonb_array_elements(r.steps) s where r.id = :'r'::uuid and s->>'name' like 'equip_city%' and (s->>'ok')::boolean")" -gt 0 ] \
   && ! grep -qE 'click gate Equip: allow (EquipConfirm|EquipNav .*slot [0-9])' <<< "$logs"; then nochange=1; fi

# the storage cleanup's safety check on the uploaded log: a Sell click outside its whitelist (there is no target for «Продать» at all)
SELL_OK_RE='click gate Sell: allow (HeroBack|CityInventory|InvEquipTab|InvBulkEnter|InvBulkCell|InvListDrag|OfferClose|UnknownBack|CityBack|EscapeBack|MapClose|ArtTab|ArtBulkEnter|ArtBulkCell|ArtListDrag)\b'
sellbad=$(grep -E 'click gate Sell: allow ' <<< "$logs" | grep -vE "$SELL_OK_RE" | head -3)
sellclicks=$(grep -cE 'click gate Sell: allow InvBulkCell' <<< "$logs")
case "$state" in done) head="Лаборатория Wardsage ($devname): всё прошло" ;; timeout) head="Лаборатория Wardsage ($devname): время вышло" ;; *) head="Лаборатория Wardsage ($devname): есть ошибки" ;; esac
[ "$nochange" = 1 ] && [ "$state" = done ] && head="Лаборатория Wardsage ($devname): предупреждение, нечего менять"
msg="$head"$'\n'"$lines"
[ "$nochange" = 1 ] && msg+="⚠️ Надеть из Города: нечего менять, сборка ${build^^} уже надета, ни одного клика по слоту. Проверка ничего не доказала."$'\n'
if [ "$state" != done ]; then   # a failed run: the first failed step and its reason go first
  fs=$(q -F '|' -v r="$run" <<< "select s->>'name', coalesce(s->>'note', '') from public.lab_runs r, jsonb_array_elements(r.steps) s where r.id = :'r'::uuid and not (s->>'ok')::boolean limit 1")
  fname1=${fs%%|*}; freason=${fs#*|}
  if [ "$fname1" = timeout ]; then top="❌ Проверка на ноутбуке: время вышло"; else top="❌ Проверка на ноутбуке: $(names "${fname1:-?}") остановилась"; fi
  msg="$top"$'\n'"Причина: ${freason:-не указана}"$'\n'"$lines"
fi
if grep -q '"sell_' <<< "$(q -v r="$run" <<< "select scenarios::text from public.lab_runs where id = :'r'::uuid" | sed 's/[{,]/"/g')"; then
  if [ -n "$sellbad" ]; then msg+="❌ ОПАСНО: клик продажи вне списка разрешённых: $(head -1 <<< "$sellbad" | cut -c1-160)"$'\n'
  else msg+="Чистка склада: «Продать» не нажималась (журнал: только разрешённые клики, отметок ячеек $sellclicks)"$'\n'; fi
fi
tot=$(q -v r="$run" <<< "select coalesce(round(extract(epoch from coalesce(finished_at, now()) - started_at))::int, 0) from public.lab_runs where id = :'r'::uuid")
[ "${tot:-0}" -gt 0 ] && msg+="Всего: $((tot / 60)) мин $((tot % 60)) с"$'\n'
[ -n "$firstreads" ] && msg+="Первое чтение экрана: $(paste -sd, <<< "$firstreads" | sed 's/,/, /g')"$'\n'
[ -n "$passes" ] && msg+="passes: $(tail -1 <<< "$passes" | sed -E 's/^passes: //')"$'\n'
if [ "$finish_ok" = 1 ]; then msg+="Можно выключать ноутбук."; else msg+="Ноутбук не подтвердил завершение: проверьте его, прежде чем выключать."; fi

# screenshots the runner took at a stop / stall (stop-<scenario>-<time>[-leadN].jpg), per scenario, with their ids
shots=$(q -F '|' -v r="$run" <<< "
  select f.name, f.id from public.lab_files f join public.lab_runs r on r.device_id = f.device_id
  where r.id = :'r'::uuid and f.name like 'stop-%' and f.created_at >= r.started_at - interval '1 minute' and f.created_at <= coalesce(r.finished_at, now()) + interval '5 minutes'
  order by f.created_at" | awk -F'|' '{n=$1; sub(/^stop-/,"",n); sub(/-[0-9]{4}-[0-9]{6}.*$/,"",n); a[n]=a[n] "  " $1 " " $2 "\n"; if(!(n in o)){o[n]=++c}} END{for(k in a) printf "%s:\n%s", k, a[k]}')
[ -n "$shots" ] && msg+="Скриншоты остановок: lab-shots.sh $run"$'\n'

# the hint frames the test app checked (src/SpriteArt.cs "frame check:" lines) cropped from the run's screenshots into one picture
fchecks=$(grep -oE 'frame check: .*' <<< "$logs" | sed -E 's/ client=.*$//' | awk '!seen[$0]++' | head -40)
fsheet=""
if [ -n "$fchecks" ] && [ -n "$shots" ]; then fsheet=$(bash "$(dirname "$0")/frames-sheet.sh" "$run" 2>/dev/null | tail -1); fi

{
  echo "# Test lab report: $devname"
  echo
  echo "- time (UTC+3): $TZ_NOW"
  echo "- run: $run (equip build ${build^^}), state: $state$([ "$nochange" = 1 ] && echo ", WARNING: nothing to change (no slot click)")"
  echo
  echo "## Steps"
  echo
  echo '```'; echo "$lines"; echo '```'
  echo
  echo "## Measurements from app_test_logs"
  echo
  echo "first read:"; echo '```'; echo "${firstreads:-none}"; echo '```'
  echo "passes:"; echo '```'; echo "${passes:-none}"; echo '```'
  if [ -n "$fchecks" ]; then echo; echo "## Frames (frame check lines; contact sheet: ${fsheet:-none, no screenshots} - tools/lab/frames-sheet.sh $run)"; echo; echo '```'; echo "$fchecks"; echo '```'; fi
  if [ -n "$shots" ]; then echo; echo "## Stop screenshots (per scenario; download: tools/lab/lab-shots.sh $run)"; echo; echo '```'; echo "$shots"; echo '```'; fi
} > "$LAB_DIR/$fname"
echo "report: $LAB_DIR/$fname"
if [ "$send" = 1 ]; then tg "$msg" && echo "telegram: sent"; else echo "--- message (not sent) ---"; echo "$msg"; fi
