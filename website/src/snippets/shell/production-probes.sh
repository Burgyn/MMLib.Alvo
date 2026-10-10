curl -sS -o /dev/null -w 'live:  %{http_code}\n' localhost:8080/health/live
curl -sS -o /dev/null -w 'ready: %{http_code}\n' localhost:8080/health/ready
