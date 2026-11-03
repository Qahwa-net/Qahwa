-- One million Notes owned by one person, for the "list with a million rows" load test (benchmarks/k6/api.js).
-- Usage:  psql "$CONNECTION" -v owner="'admin@example.com'" -f benchmarks/k6/seed-notes.sql
-- The titles are "seed note N"; clean-notes.sql removes exactly these rows.
INSERT INTO "tabNote" ("name", "creation", "modified", "modified_by", "owner", "docstatus", "idx", "title", "content")
SELECT 'SEED-' || n,
       now() - (n || ' seconds')::interval,
       now() - (n || ' seconds')::interval,
       :owner, :owner, 0, 0,
       'seed note ' || n,
       '<p>seed</p>'
FROM generate_series(1, 1000000) AS n
ON CONFLICT DO NOTHING;
ANALYZE "tabNote";
