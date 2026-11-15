using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using System.Security.Claims;
using System.Text;
using CarAgency.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Identity;
var builder=WebApplication.CreateBuilder(args);
// Prefer Render environment values; empty appsettings entries must not shadow them.
var cs = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(cs)) cs = Environment.GetEnvironmentVariable("DATABASE_URL");
if (string.IsNullOrWhiteSpace(cs)) cs = builder.Configuration.GetConnectionString("Database");
if (!string.IsNullOrWhiteSpace(cs) && (cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || cs.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)))
{
    var uri = new Uri(cs);
    var userInfo = uri.UserInfo.Split(':', 2);
    if (userInfo.Length != 2) throw new InvalidOperationException("PostgreSQL URL must include username and password.");
    var dbName = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
    var npgsql = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Database = dbName,
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = Uri.UnescapeDataString(userInfo[1]),
        SslMode = Npgsql.SslMode.Require
    };
    cs = npgsql.ConnectionString;
}
var key = Environment.GetEnvironmentVariable("JWT_KEY");
if (string.IsNullOrWhiteSpace(key)) key = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("DATABASE_CONNECTION_STRING is missing. Set it in Render > Environment.");
if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("JWT_KEY is missing. Set a random secret of at least 32 UTF-8 bytes in Render > Environment.");
if (Encoding.UTF8.GetByteCount(key)<32) throw new InvalidOperationException("JWT_KEY must be at least 32 UTF-8 bytes");
builder.Services.AddRateLimiter(o=>{o.RejectionStatusCode=429;o.AddFixedWindowLimiter("guest-submissions",p=>{p.PermitLimit=3;p.Window=TimeSpan.FromMinutes(15);p.QueueLimit=0;});});
builder.Services.AddDbContext<CarDb>(o=>o.UseNpgsql(cs));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o=>o.TokenValidationParameters=new(){ValidateIssuer=true,ValidateAudience=true,ValidateIssuerSigningKey=true,ValidateLifetime=true,ValidIssuer="CarAgency",ValidAudience="CarAgencyClients",IssuerSigningKey=new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))});
builder.Services.AddAuthorization(o=>{o.AddPolicy("AdminOnly",p=>p.RequireRole("AgencyAdmin","SuperAdmin"));o.AddPolicy("StaffOrAdmin",p=>p.RequireRole("Staff","AgencyAdmin","SuperAdmin"));});builder.Services.AddScoped<PasswordHasher<AppUser>>();
var app=builder.Build();
// Initialize the schema for a fresh dedicated Car Agency database.
// EnsureCreated does not migrate existing schemas; replace with EF migrations before upgrades.
using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<CarDb>();
    try
    {
        await database.Database.EnsureCreatedAsync();
        await database.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS "FeaturedAds" (
 "Id" uuid PRIMARY KEY,
 "Title" varchar(120) NOT NULL,
 "Subtitle" varchar(240) NOT NULL DEFAULT '',
 "ImageUrl" varchar(1000) NOT NULL,
 "TargetUrl" varchar(1000) NOT NULL DEFAULT '',
 "SortOrder" integer NOT NULL DEFAULT 0,
 "IsActive" boolean NOT NULL DEFAULT true,
 "CreatedAt" timestamptz NOT NULL DEFAULT now()
);
""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "CreatedAt" timestamptz NOT NULL DEFAULT now();""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE "Vehicles" ADD COLUMN IF NOT EXISTS "BodyType" varchar(40) NOT NULL DEFAULT 'Other';""");
        await database.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS "UsedCarSubmissions" (
 "Id" uuid PRIMARY KEY,
 "Name" varchar(100) NOT NULL,
 "Phone" varchar(25) NOT NULL,
 "Make" varchar(120) NOT NULL,
 "Year" integer NOT NULL,
 "Price" numeric(18,2) NOT NULL,
 "Description" varchar(1000) NOT NULL,
 "Status" varchar(20) NOT NULL DEFAULT 'Pending',
 "CreatedAt" timestamptz NOT NULL DEFAULT now(),
 "BodyType" varchar(40) NOT NULL DEFAULT 'Other',
 "Location" varchar(180) NOT NULL DEFAULT ''
);
""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE "UsedCarSubmissions" ADD COLUMN IF NOT EXISTS "BodyType" varchar(40) NOT NULL DEFAULT 'Other';""");
        await database.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS "UsedCarSubmissionPhotos" (
 "Id" uuid PRIMARY KEY,
 "SubmissionId" uuid NOT NULL REFERENCES "UsedCarSubmissions"("Id") ON DELETE CASCADE,
 "MimeType" varchar(30) NOT NULL,
 "ImageData" bytea NOT NULL
);
""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE "UsedCarSubmissions" ADD COLUMN IF NOT EXISTS "Email" varchar(254) NOT NULL DEFAULT '';""");
        await database.Database.ExecuteSqlRawAsync("""ALTER TABLE "UsedCarSubmissions" ADD COLUMN IF NOT EXISTS "Location" varchar(180) NOT NULL DEFAULT '';""");
        await database.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS "VehiclePhotos" (
 "Id" uuid PRIMARY KEY,
 "VehicleId" uuid NOT NULL REFERENCES "Vehicles"("Id") ON DELETE CASCADE,
 "MimeType" varchar(30) NOT NULL,
 "ImageData" bytea NOT NULL,
 "SortOrder" integer NOT NULL DEFAULT 0
);
""");
        await database.Database.ExecuteSqlRawAsync("""
CREATE TABLE IF NOT EXISTS "DemoSeedHistory" ("Key" varchar(80) PRIMARY KEY, "CreatedAt" timestamptz NOT NULL DEFAULT now());
""");
        // Seed once per database. Deleting a sample later never recreates it.
        if(!await database.Database.SqlQueryRaw<int>("""SELECT 1 AS "Value" FROM "DemoSeedHistory" WHERE "Key"='initial-six-cars'""").AnyAsync())
        {
            await using var seedTx=await database.Database.BeginTransactionAsync();
            // Use the existing administrator's agency, otherwise create a dedicated showroom.
            var agency=await database.Agencies.OrderBy(x=>x.Name=="Car Agency Main"?0:1).FirstOrDefaultAsync();
            if(agency==null){agency=new Agency{Name="Car Agency Main"};database.Agencies.Add(agency);await database.SaveChangesAsync();}
            var branch=await database.Branches.FirstOrDefaultAsync(x=>x.AgencyId==agency.Id);
            if(branch==null){branch=new Branch{AgencyId=agency.Id,Name="Main Showroom"};database.Branches.Add(branch);await database.SaveChangesAsync();}
            var samples=new (string Vin,string Make,string Model,int Year,decimal Price,string Image)[]
            {
                ("DEMO-CA-001","Toyota","Corolla",2025,1450000m,"https://images.unsplash.com/photo-1621007947382-bb3c3994e3fb?w=800&q=80"),
                ("DEMO-CA-002","BMW","3 Series",2024,2950000m,"https://images.unsplash.com/photo-1555215695-3004980ad54e?w=800&q=80"),
                ("DEMO-CA-003","Mercedes-Benz","C-Class",2025,3450000m,"https://images.unsplash.com/photo-1618843479313-40f8afb4b4d8?w=800&q=80"),
                ("DEMO-CA-004","Hyundai","Elantra",2021,850000m,"https://images.unsplash.com/photo-1549317661-bd32c8ce0db2?w=800&q=80"),
                ("DEMO-CA-005","Kia","Sportage",2020,1100000m,"https://images.unsplash.com/photo-1503376780353-7e6692767b70?w=800&q=80"),
                ("DEMO-CA-006","Nissan","Sunny",2019,520000m,"https://images.unsplash.com/photo-1494976388531-d1058494cdd8?w=800&q=80")
            };
            foreach(var s in samples)
                if(!await database.Vehicles.AnyAsync(v=>v.Vin==s.Vin))
                    database.Vehicles.Add(new Vehicle{AgencyId=agency.Id,BranchId=branch.Id,Vin=s.Vin,Make=s.Make,Model=s.Model,Year=s.Year,Price=s.Price,Cost=0,ImageUrl=s.Image,IsApproved=true,IsPublished=true,Status="Available"});
            await database.SaveChangesAsync();
            await database.Database.ExecuteSqlRawAsync("""INSERT INTO "DemoSeedHistory" ("Key") VALUES ('initial-six-cars') ON CONFLICT DO NOTHING""");
            await seedTx.CommitAsync();
            app.Logger.LogInformation("Initial six demo vehicles seeded once.");
        }
        app.Logger.LogInformation("Car Agency database schema verified.");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Car Agency database schema initialization failed.");
        // Keep health endpoints available for diagnosis; data endpoints may fail until fixed.
    }
}

using (var scope = app.Services.CreateScope())
{
 var db=scope.ServiceProvider.GetRequiredService<CarDb>();
 var username=Environment.GetEnvironmentVariable("ADMIN_USER");
 var password=Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
 if(!string.IsNullOrWhiteSpace(username)&&!string.IsNullOrWhiteSpace(password)&&password.Length>=12&& !await db.Users.AnyAsync(x=>x.Role=="SuperAdmin"))
 {
  var agency=await db.Agencies.FirstOrDefaultAsync();
  if(agency==null){agency=new Agency{Name="Car Agency Main"};db.Agencies.Add(agency);}
  var admin=new AppUser{AgencyId=agency.Id,Username=username.Trim(),Role="SuperAdmin"};
  admin.PasswordHash=scope.ServiceProvider.GetRequiredService<PasswordHasher<AppUser>>().HashPassword(admin,password);
  db.Users.Add(admin);await db.SaveChangesAsync();
  app.Logger.LogInformation("Initial super administrator provisioned.");
 }
}
// TLS is terminated by Render proxy; do not redirect internal HTTP health probes.
app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health",()=>Results.Ok(new {status="ok"}));
app.MapGet("/health/database", async (CarDb db) =>
{
    try
    {
        var connected = await db.Database.CanConnectAsync();
        return connected ? Results.Ok(new { status = "connected" }) : Results.Problem("Database connection failed", statusCode: 503);
    }
    catch
    {
        return Results.Problem("Database connection failed", statusCode: 503);
    }
});

app.MapGet("/api/public/featured-ads",async (CarDb db)=>{
 var rows=new List<object>();await using var conn=db.Database.GetDbConnection();await conn.OpenAsync();
 await using var cmd=conn.CreateCommand();cmd.CommandText="""SELECT "Id","Title","Subtitle","ImageUrl","TargetUrl" FROM "FeaturedAds" WHERE "IsActive"=true ORDER BY "SortOrder","CreatedAt" DESC LIMIT 12""";
 await using var reader=await cmd.ExecuteReaderAsync();while(await reader.ReadAsync())rows.Add(new{id=reader.GetGuid(0),title=reader.GetString(1),subtitle=reader.GetString(2),imageUrl=reader.GetString(3),targetUrl=reader.GetString(4)});return Results.Ok(rows);
});
app.MapGet("/api/admin/featured-ads",async (CarDb db)=>{
 var rows=new List<object>();await using var conn=db.Database.GetDbConnection();await conn.OpenAsync();
 await using var cmd=conn.CreateCommand();cmd.CommandText="""SELECT "Id","Title","Subtitle","ImageUrl","TargetUrl","SortOrder","IsActive" FROM "FeaturedAds" ORDER BY "SortOrder","CreatedAt" DESC""";
 await using var reader=await cmd.ExecuteReaderAsync();while(await reader.ReadAsync())rows.Add(new{id=reader.GetGuid(0),title=reader.GetString(1),subtitle=reader.GetString(2),imageUrl=reader.GetString(3),targetUrl=reader.GetString(4),sortOrder=reader.GetInt32(5),isActive=reader.GetBoolean(6)});return Results.Ok(rows);
}).RequireAuthorization("AdminOnly");
app.MapPost("/api/admin/featured-ads",async (FeaturedAdDto dto,CarDb db)=>{
 if(!ValidFeatured(dto))return Results.BadRequest(new{error="Invalid ad fields or URLs"});
 var id=Guid.NewGuid();await db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "FeaturedAds" ("Id","Title","Subtitle","ImageUrl","TargetUrl","SortOrder","IsActive") VALUES ({id},{dto.Title.Trim()},{dto.Subtitle.Trim()},{dto.ImageUrl.Trim()},{dto.TargetUrl.Trim()},{dto.SortOrder},{dto.IsActive})""");
 return Results.Created("/api/admin/featured-ads/"+id,new{id});
}).RequireAuthorization("AdminOnly");
app.MapPut("/api/admin/featured-ads/{id:guid}",async (Guid id,FeaturedAdDto dto,CarDb db)=>{
 if(!ValidFeatured(dto))return Results.BadRequest(new{error="Invalid ad fields or URLs"});
 var count=await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "FeaturedAds" SET "Title"={dto.Title.Trim()},"Subtitle"={dto.Subtitle.Trim()},"ImageUrl"={dto.ImageUrl.Trim()},"TargetUrl"={dto.TargetUrl.Trim()},"SortOrder"={dto.SortOrder},"IsActive"={dto.IsActive} WHERE "Id"={id}""");
 return count==0?Results.NotFound():Results.Ok(new{id});
}).RequireAuthorization("AdminOnly");
app.MapDelete("/api/admin/featured-ads/{id:guid}",async (Guid id,CarDb db)=>{
 var count=await db.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "FeaturedAds" WHERE "Id"={id}""");return count==0?Results.NotFound():Results.NoContent();
}).RequireAuthorization("AdminOnly");
static bool ValidFeatured(FeaturedAdDto d){
 static bool SafeUrl(string s)=>Uri.TryCreate(s,UriKind.Absolute,out var u)&&(u.Scheme=="https"||u.Scheme=="http")&&u.UserInfo.Length==0;
 return !string.IsNullOrWhiteSpace(d.Title)&&d.Title.Length<=120&&d.Subtitle.Length<=240&&d.ImageUrl.Length<=1000&&SafeUrl(d.ImageUrl)&&d.TargetUrl.Length<=1000&&(string.IsNullOrWhiteSpace(d.TargetUrl)||SafeUrl(d.TargetUrl))&&d.SortOrder>=0&&d.SortOrder<=10000;
}
// Public catalog: no credentials, no cost, VIN, customer or internal data.
app.MapGet("/api/public/vehicles",async (CarDb db,string? make,int page=1,int pageSize=12)=>{
 page=Math.Max(1,page);pageSize=Math.Clamp(pageSize,1,48);
 var q=db.Vehicles.AsNoTracking().Where(v=>v.IsPublished&&v.IsApproved&&v.Status=="Available");
 if(!string.IsNullOrWhiteSpace(make))q=q.Where(v=>v.Make==make);
 return Results.Ok(await q.OrderByDescending(v=>v.CreatedAt).ThenByDescending(v=>v.Year).ThenBy(v=>v.Id).Skip((page-1)*pageSize).Take(pageSize)
 .Select(v=>new {v.Id,v.Make,v.Model,v.Year,v.Price,v.ImageUrl,v.BranchId,v.BodyType,v.CreatedAt,IsDemo=v.Vin.StartsWith("DEMO-CA-")}).ToListAsync());
});
app.MapGet("/api/public/vehicles/{id:guid}",async (Guid id,CarDb db)=>{
 var v=await db.Vehicles.AsNoTracking().Where(v=>v.Id==id&&v.IsPublished&&v.IsApproved&&v.Status=="Available")
 .Select(v=>new {v.Id,v.Make,v.Model,v.Year,v.Price,v.ImageUrl,v.BranchId,IsDemo=v.Vin.StartsWith("DEMO-CA-")}).FirstOrDefaultAsync();
 return v==null?Results.NotFound():Results.Ok(v);
});
app.MapGet("/api/public/vehicles/{id:guid}/photos/{photoId:guid}",async (Guid id,Guid photoId,CarDb db)=>{
 if(!await db.Vehicles.AnyAsync(v=>v.Id==id&&v.IsApproved&&v.IsPublished&&v.Status=="Available"))return Results.NotFound();
 await using var conn=db.Database.GetDbConnection();await conn.OpenAsync();
 await using var cmd=conn.CreateCommand();
 cmd.CommandText="""SELECT "MimeType","ImageData" FROM "VehiclePhotos" WHERE "Id"=@photo AND "VehicleId"=@vehicle""";
 foreach(var (n,v) in new[]{("photo",photoId),("vehicle",id)}){var p=cmd.CreateParameter();p.ParameterName=n;p.Value=v;cmd.Parameters.Add(p);}
 await using var reader=await cmd.ExecuteReaderAsync();
 return await reader.ReadAsync()?Results.File((byte[])reader.GetValue(1),reader.GetString(0)):Results.NotFound();
});
app.MapGet("/api/public/vehicles/{id:guid}/photos",async (Guid id,CarDb db)=>{
 if(!await db.Vehicles.AnyAsync(v=>v.Id==id&&v.IsApproved&&v.IsPublished&&v.Status=="Available"))return Results.NotFound();
 var ids=new List<Guid>();await using var conn=db.Database.GetDbConnection();await conn.OpenAsync();
 await using var cmd=conn.CreateCommand();cmd.CommandText="""SELECT "Id" FROM "VehiclePhotos" WHERE "VehicleId"=@id ORDER BY "SortOrder" LIMIT 8""";
 var p=cmd.CreateParameter();p.ParameterName="id";p.Value=id;cmd.Parameters.Add(p);
 await using var reader=await cmd.ExecuteReaderAsync();while(await reader.ReadAsync())ids.Add(reader.GetGuid(0));
 return Results.Ok(ids.Select(photoId=>"/api/public/vehicles/"+id+"/photos/"+photoId));
});
app.MapPost("/api/auth/login",async (LoginDto dto,CarDb db,PasswordHasher<AppUser> hasher)=>{
 var u=await db.Users.FirstOrDefaultAsync(x=>x.Username==dto.Username);
 if(u==null||hasher.VerifyHashedPassword(u,u.PasswordHash,dto.Password)==PasswordVerificationResult.Failed)return Results.Unauthorized();
 var claims=new[]{new Claim(ClaimTypes.NameIdentifier,u.Id.ToString()),new Claim("agency_id",u.AgencyId.ToString()),new Claim(ClaimTypes.Role,u.Role)};
 var token=new JwtSecurityToken("CarAgency","CarAgencyClients",claims,expires:DateTime.UtcNow.AddHours(4),signingCredentials:new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),SecurityAlgorithms.HmacSha256));
 return Results.Ok(new{accessToken=new JwtSecurityTokenHandler().WriteToken(token),expiresIn=14400,role=u.Role,username=u.Username});});
app.MapPost("/api/auth/register",async (RegisterDto dto,CarDb db,PasswordHasher<AppUser> hasher)=>{
 if(string.IsNullOrWhiteSpace(dto.Username)||dto.Username.Length<3||dto.Username.Length>60||string.IsNullOrWhiteSpace(dto.Password)||dto.Password.Length<10)return Results.BadRequest();
 if(await db.Users.AnyAsync(x=>x.Username==dto.Username.Trim()))return Results.Conflict();
 var agency=new Agency{Name="Personal account"};
 var u=new AppUser{AgencyId=agency.Id,Username=dto.Username.Trim(),Role="User"};
 u.PasswordHash=hasher.HashPassword(u,dto.Password);db.Agencies.Add(agency);db.Users.Add(u);await db.SaveChangesAsync();
 return Results.Created("/api/auth/login",new {username=u.Username,role=u.Role});
}).RequireRateLimiting("guest-submissions");
app.MapGet("/api/admin/users",async (ClaimsPrincipal principal,CarDb db)=>{
 var agency=Guid.Parse(principal.FindFirst("agency_id")!.Value);
 return Results.Ok(await db.Users.Where(x=>x.AgencyId==agency).Select(x=>new{x.Id,x.Username,x.Role}).ToListAsync());
}).RequireAuthorization("AdminOnly");
app.MapPost("/api/admin/users",async (CreateUserDto dto,ClaimsPrincipal principal,CarDb db,PasswordHasher<AppUser> hasher)=>{
 var agency=Guid.Parse(principal.FindFirst("agency_id")!.Value);
 var callerRole=principal.FindFirst(ClaimTypes.Role)?.Value;
 if(dto.Role!="Staff"&&dto.Role!="AgencyAdmin")return Results.BadRequest();
 if(dto.Role=="AgencyAdmin"&&callerRole!="SuperAdmin")return Results.Forbid();
 if(string.IsNullOrWhiteSpace(dto.Username)||dto.Username.Length<3||dto.Username.Length>60||string.IsNullOrWhiteSpace(dto.Password)||dto.Password.Length<10)return Results.BadRequest();
 if(await db.Users.AnyAsync(x=>x.Username==dto.Username.Trim()))return Results.Conflict();
 var user=new AppUser{AgencyId=agency,Username=dto.Username.Trim(),Role=dto.Role};
 user.PasswordHash=hasher.HashPassword(user,dto.Password);db.Users.Add(user);await db.SaveChangesAsync();
 return Results.Created("/api/admin/users",new {user.Id,user.Username,user.Role});
}).RequireAuthorization("AdminOnly");

app.MapPut("/api/admin/users/{id:guid}",async (Guid id,UserManageDto dto,ClaimsPrincipal caller,CarDb db,PasswordHasher<AppUser> hasher)=>{
 var super=caller.IsInRole("SuperAdmin");var agency=Guid.Parse(caller.FindFirst("agency_id")!.Value);
 var user=await db.Users.FirstOrDefaultAsync(x=>x.Id==id&&(super||x.AgencyId==agency));if(user==null)return Results.NotFound();
 if(user.Role=="SuperAdmin"||dto.Role=="SuperAdmin")return Results.Forbid();
 if(dto.Role!="Staff"&&dto.Role!="AgencyAdmin")return Results.BadRequest(new{error="Invalid role"});
 if(!super&&(user.Role=="AgencyAdmin"||dto.Role=="AgencyAdmin"))return Results.Forbid();
 if(string.IsNullOrWhiteSpace(dto.Username)||dto.Username.Trim().Length<3||dto.Username.Length>60)return Results.BadRequest();
 if(await db.Users.AnyAsync(x=>x.Id!=id&&x.Username==dto.Username.Trim()))return Results.Conflict();
 if(!string.IsNullOrEmpty(dto.Password)&&dto.Password.Length<10)return Results.BadRequest(new{error="Password must have 10 characters"});
 user.Username=dto.Username.Trim();user.Role=dto.Role;
 if(!string.IsNullOrEmpty(dto.Password))user.PasswordHash=hasher.HashPassword(user,dto.Password);
 await db.SaveChangesAsync();return Results.Ok(new{user.Id,user.Username,user.Role});
}).RequireAuthorization("AdminOnly");
app.MapDelete("/api/admin/users/{id:guid}",async (Guid id,ClaimsPrincipal caller,CarDb db)=>{
 var super=caller.IsInRole("SuperAdmin");var agency=Guid.Parse(caller.FindFirst("agency_id")!.Value);
 var user=await db.Users.FirstOrDefaultAsync(x=>x.Id==id&&(super||x.AgencyId==agency));if(user==null)return Results.NotFound();
 if(user.Role=="SuperAdmin"||(!super&&user.Role=="AgencyAdmin")||caller.FindFirst(ClaimTypes.NameIdentifier)?.Value==id.ToString())return Results.Forbid();
 db.Users.Remove(user);await db.SaveChangesAsync();return Results.NoContent();
}).RequireAuthorization("AdminOnly");
api.MapPut("/agencies/{id:guid}",async (Guid id,AgencyDto dto,CarDb db)=>{
 var a=await db.Agencies.FindAsync(id);if(a==null)return Results.NotFound();
 if(string.IsNullOrWhiteSpace(dto.Name)||dto.Name.Length>120)return Results.BadRequest();a.Name=dto.Name.Trim();await db.SaveChangesAsync();return Results.Ok(new{a.Id,a.Name});
}).RequireAuthorization(p=>p.RequireRole("SuperAdmin"));
api.MapDelete("/agencies/{id:guid}",async (Guid id,CarDb db)=>{
 var a=await db.Agencies.FindAsync(id);if(a==null)return Results.NotFound();
 if(await db.Branches.AnyAsync(x=>x.AgencyId==id)||await db.Users.AnyAsync(x=>x.AgencyId==id)||await db.Vehicles.AnyAsync(x=>x.AgencyId==id)||await db.Customers.AnyAsync(x=>x.AgencyId==id)||await db.Sales.AnyAsync(x=>x.AgencyId==id)||await db.Payments.AnyAsync(x=>x.AgencyId==id)||await db.Expenses.AnyAsync(x=>x.AgencyId==id))return Results.Conflict(new{error="Agency has related records"});
 db.Agencies.Remove(a);await db.SaveChangesAsync();return Results.NoContent();
}).RequireAuthorization(p=>p.RequireRole("SuperAdmin"));
api.MapPut("/branches/{id:guid}",async (Guid id,BranchDto dto,ClaimsPrincipal caller,CarDb db)=>{
 var a=Guid.Parse(caller.FindFirst("agency_id")!.Value);var b=await db.Branches.FirstOrDefaultAsync(x=>x.Id==id&&(caller.IsInRole("SuperAdmin")||x.AgencyId==a));if(b==null)return Results.NotFound();
 if(string.IsNullOrWhiteSpace(dto.Name)||dto.Name.Length>100)return Results.BadRequest();
 b.Name=dto.Name.Trim();await db.SaveChangesAsync();return Results.Ok(new{b.Id,b.Name,b.AgencyId});
}).RequireAuthorization("AdminOnly");
api.MapDelete("/branches/{id:guid}",async (Guid id,ClaimsPrincipal caller,CarDb db)=>{
 var a=Guid.Parse(caller.FindFirst("agency_id")!.Value);var b=await db.Branches.FirstOrDefaultAsync(x=>x.Id==id&&(caller.IsInRole("SuperAdmin")||x.AgencyId==a));if(b==null)return Results.NotFound();
 if(await db.Vehicles.AnyAsync(x=>x.BranchId==id))return Results.Conflict(new{error="Branch has vehicles"});
 db.Branches.Remove(b);await db.SaveChangesAsync();return Results.NoContent();
}).RequireAuthorization("AdminOnly");

// Guest submissions are separate from approved showroom inventory.
app.MapPost("/api/public/used-submissions",async (HttpRequest request,CarDb db)=>{
 if(!request.HasFormContentType)return Results.BadRequest(new {error="multipart/form-data required"});
 var form=await request.ReadFormAsync();
 string get(string key)=>form[key].ToString().Trim();
 var name=get("name");var phone=get("phone");var email=get("email");var make=get("make");var description=get("description");var bodyType=get("bodyType");var location=get("location");
 if(!System.Net.Mail.MailAddress.TryCreate(email,out var parsedEmail)||parsedEmail.Address!=email||email.Length>254)return Results.BadRequest(new {error="يرجى إدخال بريد إلكتروني صحيح"});
 if(location.Length is < 3 or > 180||name.Length is < 2 or > 100||phone.Length is < 7 or > 25||make.Length is < 2 or > 120||description.Length is < 2 or > 1000||
 !int.TryParse(get("year"),out var year)||year<1950||year>DateTime.UtcNow.Year+1||
 !decimal.TryParse(get("price"),System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.InvariantCulture,out var price)||price<=0||price>1000000000||
 !new[]{"Sedan","SUV","Hatchback","Coupe","Pickup","Van","Convertible","Wagon","Minivan","Crossover","MPV","Truck","Other"}.Contains(bodyType))
 return Results.BadRequest(new {error="Invalid submission"});
 var photos=form.Files.GetFiles("photos");
 if(photos.Count is < 1 or > 8||photos.Any(p=>p.Length==0||p.Length>3_000_000||!new[]{"image/jpeg","image/png","image/webp"}.Contains(p.ContentType.ToLowerInvariant())))
 return Results.BadRequest(new {error="1-8 JPEG, PNG or WebP images, max 3 MB each"});
 var id=Guid.NewGuid();
 await using var connection=db.Database.GetDbConnection();await connection.OpenAsync();
 await using var transaction=await connection.BeginTransactionAsync();
 try{
 await using(var cmd=connection.CreateCommand()){
 cmd.Transaction=transaction;cmd.CommandText="""INSERT INTO "UsedCarSubmissions" ("Id","Name","Phone","Email","Make","Year","Price","Description","BodyType","Location","Status","CreatedAt") VALUES (@id,@name,@phone,@email,@make,@year,@price,@description,@body,@location,'Pending',now())""";
 void add(string n,object v){var p=cmd.CreateParameter();p.ParameterName=n;p.Value=v;cmd.Parameters.Add(p);}
 add("id",id);add("name",name);add("phone",phone);add("email",email);add("make",make);add("year",year);add("price",price);add("description",description);add("body",bodyType);add("location",location);
 await cmd.ExecuteNonQueryAsync();}
 foreach(var file in photos){
 await using var ms=new MemoryStream();await file.CopyToAsync(ms);
 var bytes=ms.ToArray();
 if(bytes.Length<12||!(bytes[0]==0xFF&&bytes[1]==0xD8||bytes[0]==0x89&&bytes[1]==0x50&&bytes[2]==0x4E&&bytes[3]==0x47||bytes[0]==0x52&&bytes[1]==0x49&&bytes[2]==0x46&&bytes[3]==0x46&&bytes[8]==0x57&&bytes[9]==0x45&&bytes[10]==0x42&&bytes[11]==0x50))
 {await transaction.RollbackAsync();return Results.BadRequest(new {error="Invalid image content"});}
 await using var cmd=connection.CreateCommand();cmd.Transaction=transaction;
 cmd.CommandText="""INSERT INTO "UsedCarSubmissionPhotos" ("Id","SubmissionId","MimeType","ImageData") VALUES (@id,@sid,@mime,@data)""";
 foreach(var (key,value) in new (string,object)[]{("id",Guid.NewGuid()),("sid",id),("mime",file.ContentType.ToLowerInvariant()),("data",bytes)}){
 var p=cmd.CreateParameter();p.ParameterName=key;p.Value=value;cmd.Parameters.Add(p);}
 await cmd.ExecuteNonQueryAsync();
 }
 await transaction.CommitAsync();return Results.Accepted(value:new {id,status="Pending",photos=photos.Count});
 }catch{await transaction.RollbackAsync();throw;}
}).DisableAntiforgery().RequireRateLimiting("guest-submissions");
// Public used-car listings are read only after administrator approval.
app.MapGet("/api/public/used-cars",async (CarDb db)=>{
 var items=new List<object>();await using var conn=db.Database.GetDbConnection();await conn.OpenAsync();
 await using var cmd=conn.CreateCommand();
 cmd.CommandText="""SELECT s."Id",s."Make",s."Year",s."Price",s."Description",s."BodyType",s."Location",s."CreatedAt", (SELECT p."Id" FROM "UsedCarSubmissionPhotos" p WHERE p."SubmissionId"=s."Id" ORDER BY p."Id" LIMIT 1) FROM "UsedCarSubmissions" s WHERE s."Status"='Approved' ORDER BY s."CreatedAt" DESC LIMIT 100""";
 await using var rd=await cmd.ExecuteReaderAsync();
 while(await rd.ReadAsync())items.Add(new{id=rd.GetGuid(0),make=rd.GetString(1),model="",year=rd.GetInt32(2),price=rd.GetDecimal(3),description=rd.GetString(4),bodyType=rd.GetString(5),location=rd.GetString(6),createdAt=rd.GetDateTime(7),photoId=rd.IsDBNull(8)?(Guid?)null:rd.GetGuid(8),condition="Used"});
 return Results.Ok(items);
});
app.MapGet("/api/public/used-cars/{id:guid}/photos/{photoId:guid}",async (Guid id,Guid photoId,CarDb db)=>{
 await using var conn=db.Database.GetDbConnection();await conn.OpenAsync();await using var cmd=conn.CreateCommand();
 cmd.CommandText="""SELECT p."MimeType",p."ImageData" FROM "UsedCarSubmissionPhotos" p JOIN "UsedCarSubmissions" s ON s."Id"=p."SubmissionId" WHERE p."Id"=@photo AND p."SubmissionId"=@id AND s."Status"='Approved'""";
 foreach(var (key,value) in new[]{("photo",photoId),("id",id)}){var param=cmd.CreateParameter();param.ParameterName=key;param.Value=value;cmd.Parameters.Add(param);}
 await using var rd=await cmd.ExecuteReaderAsync();return await rd.ReadAsync()?Results.File((byte[])rd.GetValue(1),rd.GetString(0)):Results.NotFound();
});
app.MapGet("/api/admin/used-submissions/{submissionId:guid}/photos/{photoId:guid}",async (Guid submissionId,Guid photoId,CarDb db)=>{
 await using var conn=db.Database.GetDbConnection();await conn.OpenAsync();
 await using var cmd=conn.CreateCommand();cmd.CommandText="""SELECT "MimeType","ImageData" FROM "UsedCarSubmissionPhotos" WHERE "Id"=@photo AND "SubmissionId"=@submission""";
 foreach(var (key,value) in new[]{("photo",photoId),("submission",submissionId)}){var p=cmd.CreateParameter();p.ParameterName=key;p.Value=value;cmd.Parameters.Add(p);}
 await using var reader=await cmd.ExecuteReaderAsync();if(!await reader.ReadAsync())return Results.NotFound();
 return Results.File((byte[])reader.GetValue(1),reader.GetString(0));
}).RequireAuthorization(p=>p.RequireRole("SuperAdmin"));
app.MapGet("/api/admin/used-submissions/{submissionId:guid}/photos",async (Guid submissionId,CarDb db)=>{
 var ids=new List<Guid>();
 await using var conn=db.Database.GetDbConnection();await conn.OpenAsync();
 await using var cmd=conn.CreateCommand();cmd.CommandText="""SELECT "Id" FROM "UsedCarSubmissionPhotos" WHERE "SubmissionId"=@id ORDER BY "Id" LIMIT 8""";
 var p=cmd.CreateParameter();p.ParameterName="id";p.Value=submissionId;cmd.Parameters.Add(p);
 await using var reader=await cmd.ExecuteReaderAsync();while(await reader.ReadAsync())ids.Add(reader.GetGuid(0));
 return Results.Ok(ids);
}).RequireAuthorization(p=>p.RequireRole("SuperAdmin"));
app.MapGet("/api/admin/used-submissions",async (CarDb db)=>{
 var rows=new List<object>();
 await using var conn=db.Database.GetDbConnection();await conn.OpenAsync();
 await using var cmd=conn.CreateCommand();cmd.CommandText="""SELECT "Id","Name","Phone","Make","Year","Price","Description","Status","CreatedAt","Location","BodyType","Email" FROM "UsedCarSubmissions" ORDER BY "CreatedAt" DESC LIMIT 200""";
 await using var reader=await cmd.ExecuteReaderAsync();
 while(await reader.ReadAsync())rows.Add(new{id=reader.GetGuid(0),name=reader.GetString(1),phone=reader.GetString(2),make=reader.GetString(3),year=reader.GetInt32(4),price=reader.GetDecimal(5),description=reader.GetString(6),status=reader.GetString(7),createdAt=reader.GetDateTime(8),location=reader.GetString(9),bodyType=reader.GetString(10),email=reader.GetString(11)});
 return Results.Ok(rows);
}).RequireAuthorization(p=>p.RequireRole("SuperAdmin"));
app.MapPatch("/api/admin/used-submissions/{id:guid}/status",async (Guid id,SubmissionStatusDto dto,CarDb db,IConfiguration config,ILogger<Program> logger)=>{
 if(!new[]{"Approved","Rejected","Pending"}.Contains(dto.Status))return Results.BadRequest();
 string? email=null;string? owner=null;string? car=null;
 await using(var conn=db.Database.GetDbConnection()){
 await conn.OpenAsync();
 await using(var cmd=conn.CreateCommand()){
 cmd.CommandText="""UPDATE "UsedCarSubmissions" SET "Status"=@status WHERE "Id"=@id RETURNING "Email","Name","Make" """;
 var p=cmd.CreateParameter();p.ParameterName="status";p.Value=dto.Status;cmd.Parameters.Add(p);
 p=cmd.CreateParameter();p.ParameterName="id";p.Value=id;cmd.Parameters.Add(p);
 await using var reader=await cmd.ExecuteReaderAsync();
 if(!await reader.ReadAsync())return Results.NotFound();
 email=reader.GetString(0);owner=reader.GetString(1);car=reader.GetString(2);
 }}
 var notified=false;
 if(dto.Status!="Pending"&&!string.IsNullOrWhiteSpace(email)){
 try{
 var host=config["SMTP_HOST"];var sender=config["SMTP_FROM"];var username=config["SMTP_USER"];var password=config["SMTP_PASSWORD"];
 if(!string.IsNullOrWhiteSpace(host)&&!string.IsNullOrWhiteSpace(sender)){
 using var mail=new System.Net.Mail.MailMessage(sender,email);
 mail.Subject=dto.Status=="Approved"?"Car Agency - تمت الموافقة على إعلانك":"Car Agency - تم رفض إعلانك";
 mail.Body=$"مرحبًا {owner}،\nحالة إعلان السيارة {car}: "+(dto.Status=="Approved"?"تمت الموافقة":"تم الرفض")+".\nرقم الطلب: "+id;
 using var smtp=new System.Net.Mail.SmtpClient(host,int.TryParse(config["SMTP_PORT"],out var port)?port:587){EnableSsl=true,DeliveryMethod=System.Net.Mail.SmtpDeliveryMethod.Network};
 if(!string.IsNullOrWhiteSpace(username))smtp.Credentials=new System.Net.NetworkCredential(username,password);
 await smtp.SendMailAsync(mail);notified=true;
 }else logger.LogWarning("SMTP settings are missing; submission {SubmissionId} status saved without email",id);
 }catch(Exception ex){logger.LogError(ex,"Could not send submission status email for {SubmissionId}",id);}
 }
 return Results.Ok(new{id,status=dto.Status,emailSent=notified});
}).RequireAuthorization(p=>p.RequireRole("SuperAdmin"));
var api=app.MapGroup("/api").RequireAuthorization("StaffOrAdmin");
api.MapGet("/dashboard",async (ClaimsPrincipal user,CarDb db)=>{var a=Guid.Parse(user.FindFirst("agency_id")!.Value);var all=user.IsInRole("SuperAdmin");return Results.Ok(new{vehicles=await db.Vehicles.CountAsync(x=>all||x.AgencyId==a),available=await db.Vehicles.CountAsync(x=>(all||x.AgencyId==a)&&x.Status=="Available"),customers=await db.Customers.CountAsync(x=>all||x.AgencyId==a),sales=await db.Sales.CountAsync(x=>all||x.AgencyId==a)});});
api.MapGet("/vehicles/{id:guid}/photos",async (Guid id,ClaimsPrincipal user,CarDb db)=>{
 var agency=Guid.Parse(user.FindFirst("agency_id")!.Value);
 if(!await db.Vehicles.AnyAsync(v=>v.Id==id&&(user.IsInRole("SuperAdmin")||v.AgencyId==agency)))return Results.NotFound();
 var ids=new List<Guid>();
 await using var conn=db.Database.GetDbConnection();await conn.OpenAsync();
 await using var cmd=conn.CreateCommand();cmd.CommandText="""SELECT "Id" FROM "VehiclePhotos" WHERE "VehicleId"=@vehicle ORDER BY "SortOrder" LIMIT 8""";
 var p=cmd.CreateParameter();p.ParameterName="vehicle";p.Value=id;cmd.Parameters.Add(p);
 await using var reader=await cmd.ExecuteReaderAsync();while(await reader.ReadAsync())ids.Add(reader.GetGuid(0));
 return Results.Ok(ids);
}).RequireAuthorization("AdminOnly");
api.MapGet("/vehicles/{id:guid}/photos/{photoId:guid}",async (Guid id,Guid photoId,ClaimsPrincipal user,CarDb db)=>{
 var agency=Guid.Parse(user.FindFirst("agency_id")!.Value);
 if(!await db.Vehicles.AnyAsync(v=>v.Id==id&&(user.IsInRole("SuperAdmin")||v.AgencyId==agency)))return Results.NotFound();
 await using var conn=db.Database.GetDbConnection();await conn.OpenAsync();
 await using var cmd=conn.CreateCommand();cmd.CommandText="""SELECT "MimeType","ImageData" FROM "VehiclePhotos" WHERE "Id"=@photo AND "VehicleId"=@vehicle""";
 foreach(var (key,value) in new[]{("photo",photoId),("vehicle",id)}){var p=cmd.CreateParameter();p.ParameterName=key;p.Value=value;cmd.Parameters.Add(p);}
 await using var reader=await cmd.ExecuteReaderAsync();
 return await reader.ReadAsync()?Results.File((byte[])reader.GetValue(1),reader.GetString(0)):Results.NotFound();
}).RequireAuthorization("AdminOnly");
api.MapGet("/vehicles",async (ClaimsPrincipal user,CarDb db)=>{var a=Guid.Parse(user.FindFirst("agency_id")!.Value);var isSuper=user.IsInRole("SuperAdmin");return await db.Vehicles.Where(x=>isSuper||x.AgencyId==a).OrderBy(x=>x.Make).ToListAsync();});
api.MapPatch("/vehicles/{id:guid}/publication",async (Guid id,PublicationDto dto,ClaimsPrincipal user,CarDb db)=>{
 var agency=Guid.Parse(user.FindFirst("agency_id")!.Value);
 var v=await db.Vehicles.FirstOrDefaultAsync(x=>x.Id==id&&(user.FindFirst(ClaimTypes.Role)!.Value=="SuperAdmin"||x.AgencyId==agency));
 if(v==null)return Results.NotFound();
 v.IsPublished=dto.IsPublished;
 // Approval is separate and cannot be changed through publication endpoint.
 await db.SaveChangesAsync();return Results.Ok(new {v.Id,v.IsPublished,v.IsApproved});
}).RequireAuthorization("AdminOnly");
api.MapPatch("/vehicles/{id:guid}/approval",async (Guid id,ApprovalDto dto,ClaimsPrincipal user,CarDb db)=>{
 var agency=Guid.Parse(user.FindFirst("agency_id")!.Value);
 var v=await db.Vehicles.FirstOrDefaultAsync(x=>x.Id==id&&(user.FindFirst(ClaimTypes.Role)!.Value=="SuperAdmin"||x.AgencyId==agency));
 if(v==null)return Results.NotFound();
 v.IsApproved=dto.IsApproved;await db.SaveChangesAsync();return Results.Ok(new {v.Id,v.IsPublished,v.IsApproved});
}).RequireAuthorization("AdminOnly");
api.MapPost("/vehicles",async (VehicleDto dto,ClaimsPrincipal user,CarDb db)=>{var a=Guid.Parse(user.FindFirst("agency_id")!.Value);var branch=await db.Branches.FirstOrDefaultAsync(x=>x.Id==dto.BranchId);if(branch==null||(user.FindFirst(ClaimTypes.Role)?.Value!="SuperAdmin"&&branch.AgencyId!=a))return Results.BadRequest("Invalid branch");a=branch.AgencyId;if(dto.Year<1900||dto.Price<0||string.IsNullOrWhiteSpace(dto.Vin))return Results.BadRequest("Invalid vehicle");var v=new Vehicle{AgencyId=a,BranchId=dto.BranchId,Vin=dto.Vin.Trim(),Make=dto.Make,Model=dto.Model,Year=dto.Year,Cost=dto.Cost,Price=dto.Price};db.Vehicles.Add(v);await db.SaveChangesAsync();return Results.Created($"/api/vehicles/{v.Id}",v);}).RequireAuthorization("AdminOnly");
// Super administrators can manage all agencies; ordinary managers remain agency-scoped.
api.MapGet("/branches",async (ClaimsPrincipal user,CarDb db)=>{
 var role=user.FindFirst(ClaimTypes.Role)?.Value;var a=Guid.Parse(user.FindFirst("agency_id")!.Value);
 return await db.Branches.AsNoTracking().Where(b=>role=="SuperAdmin"||b.AgencyId==a).OrderBy(b=>b.Name).Select(b=>new{b.Id,b.AgencyId,b.Name}).ToListAsync();
});
api.MapPost("/branches",async (BranchDto dto,ClaimsPrincipal user,CarDb db)=>{
 var role=user.FindFirst(ClaimTypes.Role)?.Value;var a=Guid.Parse(user.FindFirst("agency_id")!.Value);
 var target=role=="SuperAdmin"&&dto.AgencyId.HasValue?dto.AgencyId.Value:a;
 if(!await db.Agencies.AnyAsync(x=>x.Id==target)||string.IsNullOrWhiteSpace(dto.Name)||dto.Name.Length>100)return Results.BadRequest();
 var branch=new Branch{AgencyId=target,Name=dto.Name.Trim()};db.Branches.Add(branch);await db.SaveChangesAsync();return Results.Created("/api/branches",new{branch.Id,branch.AgencyId,branch.Name});
}).RequireAuthorization("AdminOnly");
api.MapGet("/agencies",async (ClaimsPrincipal user,CarDb db)=>{
 var role=user.FindFirst(ClaimTypes.Role)?.Value;var a=Guid.Parse(user.FindFirst("agency_id")!.Value);
 return await db.Agencies.AsNoTracking().Where(x=>role=="SuperAdmin"||x.Id==a).Select(x=>new{x.Id,x.Name}).ToListAsync();
}).RequireAuthorization("AdminOnly");
api.MapPost("/agencies",async (AgencyDto dto,CarDb db)=>{
 if(string.IsNullOrWhiteSpace(dto.Name)||dto.Name.Length>120)return Results.BadRequest();
 var a=new Agency{Name=dto.Name.Trim()};db.Agencies.Add(a);await db.SaveChangesAsync();return Results.Created("/api/agencies",new{a.Id,a.Name});
}).RequireAuthorization(p=>p.RequireRole("SuperAdmin"));
api.MapPut("/vehicles/{id:guid}",async (Guid id,VehicleEditDto dto,ClaimsPrincipal user,CarDb db)=>{
 var agency=Guid.Parse(user.FindFirst("agency_id")!.Value);
 var v=await db.Vehicles.FirstOrDefaultAsync(x=>x.Id==id&&(user.IsInRole("SuperAdmin")||x.AgencyId==agency));
 if(v==null)return Results.NotFound();
 if(string.IsNullOrWhiteSpace(dto.Make)||string.IsNullOrWhiteSpace(dto.Model)||dto.Year<1900||dto.Year>DateTime.UtcNow.Year+2||dto.Price<0||dto.Cost<0)return Results.BadRequest(new{error="Invalid vehicle details"});
 v.Make=dto.Make.Trim();v.Model=dto.Model.Trim();v.Year=dto.Year;v.Price=dto.Price;v.Cost=dto.Cost;
 await db.SaveChangesAsync();return Results.Ok(new{v.Id,v.Make,v.Model,v.Year,v.Price,v.Cost});
}).RequireAuthorization("AdminOnly");
api.MapDelete("/vehicles/{id:guid}",async (Guid id,ClaimsPrincipal user,CarDb db)=>{
 var role=user.FindFirst(ClaimTypes.Role)?.Value;var agency=Guid.Parse(user.FindFirst("agency_id")!.Value);
 var v=await db.Vehicles.FirstOrDefaultAsync(x=>x.Id==id&&(role=="SuperAdmin"||x.AgencyId==agency));
 if(v==null)return Results.NotFound();
 if(await db.Sales.AnyAsync(x=>x.VehicleId==id))return Results.Conflict(new{error="Vehicle has sales records; archive it instead"});
 db.Vehicles.Remove(v);await db.SaveChangesAsync();return Results.NoContent();
}).RequireAuthorization("AdminOnly");
api.MapGet("/admin/vehicles-all",async (ClaimsPrincipal user,CarDb db)=>{
 var role=user.FindFirst(ClaimTypes.Role)?.Value;var a=Guid.Parse(user.FindFirst("agency_id")!.Value);
 return await db.Vehicles.AsNoTracking().Where(v=>role=="SuperAdmin"||v.AgencyId==a).OrderByDescending(v=>v.Year).Take(500).ToListAsync();
}).RequireAuthorization("AdminOnly");
api.MapPost("/vehicles/{id:guid}/photos",async (Guid id,HttpRequest request,ClaimsPrincipal user,CarDb db)=>{
 var agency=Guid.Parse(user.FindFirst("agency_id")!.Value);
 if(!await db.Vehicles.AnyAsync(v=>v.Id==id&&(user.IsInRole("SuperAdmin")||v.AgencyId==agency)))return Results.NotFound();
 if(!request.HasFormContentType)return Results.BadRequest(new{error="multipart/form-data required"});
 var form=await request.ReadFormAsync();var photos=form.Files.GetFiles("photos");
 if(photos.Count is < 1 or > 8||photos.Any(p=>p.Length is < 12 or > 3000000||!new[]{"image/jpeg","image/png","image/webp"}.Contains(p.ContentType.ToLowerInvariant())))return Results.BadRequest(new{error="Choose 1-8 JPG/PNG/WebP photos, maximum 3MB each"});
 var files=new List<(string Mime,byte[] Data)>();
 foreach(var file in photos){await using var stream=new MemoryStream();await file.CopyToAsync(stream);var bytes=stream.ToArray();
 var valid=file.ContentType.ToLowerInvariant() switch {
 "image/jpeg"=>bytes[0]==255&&bytes[1]==216,
 "image/png"=>bytes[0]==137&&bytes[1]==80&&bytes[2]==78&&bytes[3]==71,
 "image/webp"=>bytes[0]==82&&bytes[1]==73&&bytes[2]==70&&bytes[3]==70&&bytes[8]==87&&bytes[9]==69&&bytes[10]==66&&bytes[11]==80,
 _=>false};
 if(!valid)return Results.BadRequest(new{error="Invalid image data"});files.Add((file.ContentType.ToLowerInvariant(),bytes));}
 await using var conn=db.Database.GetDbConnection();await conn.OpenAsync();await using var tx=await conn.BeginTransactionAsync();
 try{
 await using(var del=conn.CreateCommand()){del.Transaction=tx;del.CommandText="""DELETE FROM "VehiclePhotos" WHERE "VehicleId"=@id""";var p=del.CreateParameter();p.ParameterName="id";p.Value=id;del.Parameters.Add(p);await del.ExecuteNonQueryAsync();}
 for(var i=0;i<files.Count;i++){await using var cmd=conn.CreateCommand();cmd.Transaction=tx;cmd.CommandText="""INSERT INTO "VehiclePhotos" ("Id","VehicleId","MimeType","ImageData","SortOrder") VALUES (@id,@vehicle,@mime,@data,@sort)""";
 foreach(var (n,v) in new (string,object)[]{("id",Guid.NewGuid()),("vehicle",id),("mime",files[i].Mime),("data",files[i].Data),("sort",i)}){var p=cmd.CreateParameter();p.ParameterName=n;p.Value=v;cmd.Parameters.Add(p);}await cmd.ExecuteNonQueryAsync();}
 await tx.CommitAsync();return Results.Ok(new{vehicleId=id,count=files.Count});
 }catch{await tx.RollbackAsync();throw;}
}).DisableAntiforgery().RequireAuthorization("AdminOnly");
api.MapGet("/customers",async (ClaimsPrincipal user,CarDb db)=>{var a=Guid.Parse(user.FindFirst("agency_id")!.Value);return await db.Customers.Where(x=>x.AgencyId==a).ToListAsync();});
api.MapPost("/customers",async (CustomerDto dto,ClaimsPrincipal user,CarDb db)=>{var a=Guid.Parse(user.FindFirst("agency_id")!.Value);var c=new Customer{AgencyId=a,FullName=dto.FullName,Phone=dto.Phone};db.Customers.Add(c);await db.SaveChangesAsync();return Results.Created($"/api/customers/{c.Id}",c);});
app.Run();
record SubmissionStatusDto(string Status);
record BranchDto(string Name,Guid? AgencyId);
record AgencyDto(string Name);
record RegisterDto(string Username,string Password);
record CreateUserDto(string Username,string Password,string Role);
record LoginDto(string Username,string Password);
record VehicleDto(Guid BranchId,string Vin,string Make,string Model,int Year,decimal Cost,decimal Price);
record CustomerDto(string FullName,string Phone);

record PublicationDto(bool IsPublished);
record ApprovalDto(bool IsApproved);


record VehicleEditDto(string Make,string Model,int Year,decimal Price,decimal Cost);

record UserManageDto(string Username,string Role,string? Password);

record FeaturedAdDto(string Title,string Subtitle,string ImageUrl,string TargetUrl,int SortOrder,bool IsActive);
