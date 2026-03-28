-- ============================================================
-- F0-03 · GAIA MySQL export query
-- Databáze: GAIA MySQL (gaia_user@172.24.0.12 nebo příslušná GAIA DB)
-- Spustit PŘED F0-03-populate-staging.py (nebo použít přímo v Python scriptu)
-- Exportuje pipe_organizations → CSV nebo předat do populate-staging.py
-- ============================================================

-- Základní export pro staging
-- Výsledek: pipe_id, pipe_type, partner_id (NULL pokud -1), partner_region, role_label, country_label, org_name
SELECT
    po.pipe_id,
    po.pipe_type,
    CASE WHEN po.partner_id > 0 THEN po.partner_id ELSE NULL END AS partner_id,
    po.partner AS partner_region,
    po.role AS role_label,
    po.country AS country_label,
    po.name AS org_name,
    CASE WHEN po.partner_id > 0 THEN 'pending' ELSE 'no_partner_id' END AS match_status
FROM pipe_organizations po
WHERE po.pipe_id IS NOT NULL
ORDER BY po.pipe_type, po.partner, po.partner_id;

-- ============================================================
-- Statistiky před exportem (ověření kompletnosti dat)
-- ============================================================

-- Celkový počet organizací per instance
SELECT
    pipe_type,
    COUNT(*) AS total_orgs,
    SUM(CASE WHEN partner_id > 0 THEN 1 ELSE 0 END) AS with_partner_id,
    SUM(CASE WHEN partner_id <= 0 OR partner_id IS NULL THEN 1 ELSE 0 END) AS without_partner_id
FROM pipe_organizations
GROUP BY pipe_type
ORDER BY pipe_type;

-- Distribuce rolí per instance (pro validaci role mappingu)
SELECT
    pipe_type,
    role AS role_label,
    COUNT(*) AS count
FROM pipe_organizations
WHERE pipe_id IS NOT NULL
GROUP BY pipe_type, role
ORDER BY pipe_type, count DESC;

-- Distribuce regionů (partner sloupec)
SELECT
    pipe_type,
    partner AS region,
    COUNT(*) AS count
FROM pipe_organizations
WHERE pipe_id IS NOT NULL
GROUP BY pipe_type, partner
ORDER BY pipe_type, partner;

-- Záznamy BEZ partner_id (budou manuálně zpracovány)
SELECT
    po.pipe_id,
    po.pipe_type,
    po.name,
    po.role,
    po.country,
    po.owner
FROM pipe_organizations po
WHERE (po.partner_id IS NULL OR po.partner_id <= 0)
  AND po.pipe_id IS NOT NULL
ORDER BY po.pipe_type, po.name;
