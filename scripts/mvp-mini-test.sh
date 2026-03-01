#!/usr/bin/env bash
set -euo pipefail
set +H

API_URL="${API_URL:-http://localhost:9000/api}"
LOGIN_EMAIL="${LOGIN_EMAIL:-admin@gentlesuite.local}"
LOGIN_PASSWORD="${LOGIN_PASSWORD:-Password123!}"

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

json_get() {
  local key="$1"
  node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{const j=JSON.parse(s);const v=j['$key'];if(typeof v==='undefined'||v===null)process.stdout.write('');else process.stdout.write(String(v));}catch{process.stdout.write('')}})"
}

json_len() {
  node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{const j=JSON.parse(s);if(Array.isArray(j))process.stdout.write(String(j.length));else if(Array.isArray(j.items))process.stdout.write(String(j.items.length));else process.stdout.write('0');}catch{process.stdout.write('0')}})"
}

path_exists() {
  local path="$1"
  node -e "const fs=require('fs');const j=JSON.parse(fs.readFileSync(process.argv[1],'utf8'));process.stdout.write(j.paths[process.argv[2]]?'1':'0')" "$TMP_DIR/swagger.json" "$path"
}

upsert_range() {
  local entity="$1"
  local year="$2"
  local prefix="$3"
  local next="$4"
  local padding="$5"
  curl -s -o /dev/null -X PUT "$API_URL/Settings/number-ranges" \
    -H "Authorization: Bearer $TOKEN" \
    -H 'Content-Type: application/json' \
    -d "{\"entityType\":\"$entity\",\"year\":$year,\"prefix\":\"$prefix\",\"nextValue\":$next,\"padding\":$padding}"
}

echo "== MVP Mini-Test =="
echo "API_URL=$API_URL"

curl -s "${API_URL%/api}/swagger/v1/swagger.json" > "$TMP_DIR/swagger.json"

LOGIN_PAYLOAD="$(cat <<JSON
{"email":"$LOGIN_EMAIL","password":"$LOGIN_PASSWORD"}
JSON
)"
LOGIN_RESP="$(curl -s -X POST "$API_URL/Auth/login" -H 'Content-Type: application/json' -d "$LOGIN_PAYLOAD")"
TOKEN="$(printf '%s' "$LOGIN_RESP" | json_get token)"

if [[ -z "$TOKEN" ]]; then
  echo "AUTH=FAIL"
  exit 1
fi

TS="$(date +%s)"
PHONE_SUFFIX="$((RANDOM%900000+100000))"
TEST_PHONE="+49170${PHONE_SUFFIX}"
YEAR="$(date +%Y)"

if [[ "$(path_exists '/api/Settings/number-ranges')" == "1" ]]; then
  BASE_NEXT="$((50000 + (TS % 10000)))"
  upsert_range "Customer" "$YEAR" "KD" "$BASE_NEXT" 4
  upsert_range "Quote" "$YEAR" "AG" "$BASE_NEXT" 4
  upsert_range "Invoice" "$YEAR" "RE" "$BASE_NEXT" 4
fi

# Test 1: customer + duplicate
CUST_PAYLOAD="$(cat <<JSON
{"companyName":"MVP Test GmbH $TS","industry":"IT","website":"https://example.com","taxId":"123/456/78901","vatId":"DE123456789","primaryContact":{"firstName":"Max","lastName":"Tester","email":"max.$TS@example.com","phone":"$TEST_PHONE","position":"CEO"},"primaryLocation":{"label":"HQ","street":"Teststr 1","city":"Berlin","zipCode":"10115","country":"Deutschland"}}
JSON
)"
C1_CODE="$(curl -s -o "$TMP_DIR/t1_c1.json" -w '%{http_code}' -X POST "$API_URL/Customers" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$CUST_PAYLOAD")"
C2_CODE="$(curl -s -o "$TMP_DIR/t1_c2.json" -w '%{http_code}' -X POST "$API_URL/Customers" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$CUST_PAYLOAD")"
C1_ID="$(cat "$TMP_DIR/t1_c1.json" | json_get id)"
CHECK_DUP_ENDPOINT="$(path_exists '/api/Customers/check-duplicate')"
C1_ERR="$(cat "$TMP_DIR/t1_c1.json" | json_get error)"
C2_ERR="$(cat "$TMP_DIR/t1_c2.json" | json_get error)"

# Test 2: quote + pdf + send
Q_PAYLOAD="$(cat <<JSON
{"customerId":"$C1_ID","subject":"MVP Angebot $TS","taxRate":19,"taxMode":0,"lines":[{"title":"Setup","description":"Einrichtung","quantity":2,"unitPrice":100,"discountPercent":10,"lineType":0,"vatPercent":19,"sortOrder":0},{"title":"Support","description":"Monatlich","quantity":1,"unitPrice":80,"discountPercent":5,"lineType":1,"vatPercent":19,"sortOrder":1},{"title":"Audit","description":"Analyse","quantity":3,"unitPrice":50,"discountPercent":0,"lineType":0,"vatPercent":19,"sortOrder":2}]}
JSON
)"
Q1_CODE="$(curl -s -o "$TMP_DIR/t2_q1.json" -w '%{http_code}' -X POST "$API_URL/Quotes" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "$Q_PAYLOAD")"
Q1_ID="$(cat "$TMP_DIR/t2_q1.json" | json_get id)"
Q1_LINES="$(cat "$TMP_DIR/t2_q1.json" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{const j=JSON.parse(s);process.stdout.write(String((j.lines||[]).length))}catch{process.stdout.write('0')}})")"
PDF_CODE="$(curl -s -o "$TMP_DIR/t2_q1.pdf" -w '%{http_code}' -H "Authorization: Bearer $TOKEN" "$API_URL/Quotes/$Q1_ID/pdf")"
PDF_SIZE="$(wc -c < "$TMP_DIR/t2_q1.pdf" | tr -d ' ')"
SEND_CODE="$(curl -s -o "$TMP_DIR/t2_send.json" -w '%{http_code}' -X POST "$API_URL/Quotes/$Q1_ID/send" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "{\"recipientEmail\":\"max.$TS@example.com\",\"message\":\"Bitte pruefen\",\"requireSignature\":true,\"expirationDays\":30}")"

# Test 3: quote versions
NEW_VER_ENDPOINT="$(path_exists '/api/Quotes/{id}/new-version')"
VERSIONS_ENDPOINT="$(path_exists '/api/Quotes/{id}/versions')"
T3_NEW_VERSION_CODE="NA"
T3_VERS_COUNT="NA"
T3_CURRENT_COUNT="NA"
if [[ "$NEW_VER_ENDPOINT" == "1" && "$VERSIONS_ENDPOINT" == "1" && -n "$Q1_ID" ]]; then
  T3_NEW_VERSION_CODE="$(curl -s -o "$TMP_DIR/t3_newver.json" -w '%{http_code}' -X POST "$API_URL/Quotes/$Q1_ID/new-version" -H "Authorization: Bearer $TOKEN")"
  Q2_ID="$(cat "$TMP_DIR/t3_newver.json" | json_get id)"
  if [[ -n "$Q2_ID" ]]; then
    curl -s -H "Authorization: Bearer $TOKEN" "$API_URL/Quotes/$Q2_ID/versions" > "$TMP_DIR/t3_versions.json"
    T3_VERS_COUNT="$(cat "$TMP_DIR/t3_versions.json" | json_len)"
    T3_CURRENT_COUNT="$(cat "$TMP_DIR/t3_versions.json" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{const j=JSON.parse(s);process.stdout.write(String(j.filter(x=>x.isCurrentVersion).length))}catch{process.stdout.write('0')}})")"
  fi
fi

# Test 4: quote -> invoice -> payment
CONV_CODE="$(curl -s -o "$TMP_DIR/t4_conv.json" -w '%{http_code}' -X POST "$API_URL/Quotes/$Q1_ID/convert-to-invoice" -H "Authorization: Bearer $TOKEN")"
INV_ID="$(cat "$TMP_DIR/t4_conv.json" | json_get id)"
FINALIZE_CODE="NA"
PAY_CODE="NA"
STATUS_AFTER_PAY="NA"
PAY_ERR=""
if [[ -n "$INV_ID" ]]; then
  FINALIZE_CODE="$(curl -s -o "$TMP_DIR/t4_finalize.json" -w '%{http_code}' -X POST "$API_URL/Invoices/$INV_ID/finalize" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"sendEmail":false}')"
  INV_JSON="$(curl -s -H "Authorization: Bearer $TOKEN" "$API_URL/Invoices/$INV_ID")"
  OPEN_AMOUNT="$(printf '%s' "$INV_JSON" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>{try{const j=JSON.parse(s);process.stdout.write(String(j.openAmount||j.grossTotal||0))}catch{process.stdout.write('0')}})")"
  PAY_CODE="$(curl -s -o "$TMP_DIR/t4_pay.json" -w '%{http_code}' -X POST "$API_URL/Invoices/$INV_ID/payment" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d "{\"amount\":$OPEN_AMOUNT,\"paymentMethod\":\"BankTransfer\",\"reference\":\"MVP-$TS\"}")"
  PAY_ERR="$(cat "$TMP_DIR/t4_pay.json" | json_get error)"
  STATUS_AFTER_PAY="$(curl -s -H "Authorization: Bearer $TOKEN" "$API_URL/Invoices/$INV_ID" | json_get status)"
fi

# Test 5: cancellation
CANCEL_CODE="NA"
ORIG_NO=""
CANCEL_NO=""
if [[ -n "$INV_ID" ]]; then
  ORIG_NO="$(curl -s -H "Authorization: Bearer $TOKEN" "$API_URL/Invoices/$INV_ID" | json_get invoiceNumber)"
  CANCEL_CODE="$(curl -s -o "$TMP_DIR/t5_cancel.json" -w '%{http_code}' -X POST "$API_URL/Invoices/$INV_ID/cancel" -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"reason":"MVP Test"}')"
  CANCEL_ID="$(cat "$TMP_DIR/t5_cancel.json" | json_get id)"
  if [[ -n "$CANCEL_ID" ]]; then
    CANCEL_NO="$(curl -s -H "Authorization: Bearer $TOKEN" "$API_URL/Invoices/$CANCEL_ID" | json_get invoiceNumber)"
  fi
fi

# Test 6-8 capability checks
REMINDER_SETTINGS_ENDPOINT="$(path_exists '/api/Settings/reminders')"
INV_REMSTOP_ENDPOINT="$(path_exists '/api/Invoices/{id}/reminder-stop')"
CUST_REMSTOP_ENDPOINT="$(path_exists '/api/Customers/{id}/reminder-stop')"
SUBS_ENDPOINT="$(path_exists '/api/Subscriptions')"

echo "T1_CREATE_CODE=$C1_CODE"
echo "T1_DUP_CREATE_CODE=$C2_CODE"
echo "T1_CREATE_ERR=$C1_ERR"
echo "T1_DUP_CREATE_ERR=$C2_ERR"
echo "T1_CHECK_DUP_ENDPOINT=$CHECK_DUP_ENDPOINT"
echo "T2_QUOTE_CREATE_CODE=$Q1_CODE"
echo "T2_LINES=$Q1_LINES"
echo "T2_PDF_CODE=$PDF_CODE"
echo "T2_PDF_SIZE=$PDF_SIZE"
echo "T2_SEND_CODE=$SEND_CODE"
echo "T3_NEW_VERSION_ENDPOINT=$NEW_VER_ENDPOINT"
echo "T3_VERSIONS_ENDPOINT=$VERSIONS_ENDPOINT"
echo "T3_NEW_VERSION_CODE=$T3_NEW_VERSION_CODE"
echo "T3_VERS_COUNT=$T3_VERS_COUNT"
echo "T3_CURRENT_COUNT=$T3_CURRENT_COUNT"
echo "T4_CONVERT_CODE=$CONV_CODE"
echo "T4_FINALIZE_CODE=$FINALIZE_CODE"
echo "T4_PAY_CODE=$PAY_CODE"
echo "T4_PAY_ERR=$PAY_ERR"
echo "T4_STATUS_AFTER_PAY=$STATUS_AFTER_PAY"
echo "T5_CANCEL_CODE=$CANCEL_CODE"
echo "T5_ORIG_NO=$ORIG_NO"
echo "T5_CANCEL_NO=$CANCEL_NO"
echo "T6_SUBSCRIPTIONS_ENDPOINT=$SUBS_ENDPOINT"
echo "T8_REMINDER_SETTINGS_ENDPOINT=$REMINDER_SETTINGS_ENDPOINT"
echo "T8_INVOICE_REMSTOP_ENDPOINT=$INV_REMSTOP_ENDPOINT"
echo "T8_CUSTOMER_REMSTOP_ENDPOINT=$CUST_REMSTOP_ENDPOINT"
