-- Run once against an existing v0.3 database (EF-generated default names).
ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "IsPublished" boolean NOT NULL DEFAULT false;
ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "IsApproved" boolean NOT NULL DEFAULT false;
ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "ImageUrl" text NULL;
CREATE INDEX IF NOT EXISTS "IX_Vehicles_PublicCatalog" ON "Vehicles" ("IsPublished", "IsApproved", "Status");
