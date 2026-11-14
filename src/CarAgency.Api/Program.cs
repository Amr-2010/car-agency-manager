using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CarAgency.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Identity;
var builder=WebApplication.CreateBuilder(args);
var cs=builder.Configuration.GetConnectionString("Database") ?? Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING") ?? throw new InvalidOperationException("Database connection required");
var key=builder.Configuration["Jwt:Key"] ?? Environment.GetEnvironmentVariable("JWT_KEY") ?? throw new InvalidOperationException("JWT_KEY required");
if(Encoding.UTF8.GetByteCount(key)<32) throw new InvalidOperationException("JWT_KEY must be >= 32 bytes");
builder.Services.AddDbContext<CarDb>(o=>o.UseNpgsql(cs));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o=>o.TokenValidationParameters=new(){ValidateIssuer=true,ValidateAudience=true,ValidateIssuerSigningKey=true,ValidateLifetime=true,ValidIssuer="CarAgency",ValidAudience="CarAgencyClients",IssuerSigningKey=new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key))});
builder.Services.AddAuthorization(o=>{o.AddPolicy("AdminOnly",p=>p.RequireRole("AgencyAdmin","SuperAdmin"));o.AddPolicy("StaffOrAdmin",p=>p.RequireRole("Staff","AgencyAdmin","SuperAdmin"));});builder.Services.AddScoped<PasswordHasher<AppUser>>();
var app=builder.Build();app.UseHttpsRedirection();app.UseAuthentication();app.UseAuthorization();
app.MapGet("/health",()=>Results.Ok(new {status="ok"}));
// Public catalog: no credentials, no cost, VIN, customer or internal data.
app.MapGet("/api/public/vehicles",async (CarDb db,string? make,int page=1,int pageSize=12)=>{
 page=Math.Max(1,page);pageSize=Math.Clamp(pageSize,1,48);
 var q=db.Vehicles.AsNoTracking().Where(v=>v.IsPublished&&v.IsApproved&&v.Status=="Available");
 if(!string.IsNullOrWhiteSpace(make))q=q.Where(v=>v.Make==make);
 return Results.Ok(await q.OrderByDescending(v=>v.Year).ThenBy(v=>v.Id).Skip((page-1)*pageSize).Take(pageSize)
 .Select(v=>new {v.Id,v.Make,v.Model,v.Year,v.Price,v.ImageUrl,v.BranchId}).ToListAsync());
});
app.MapGet("/api/public/vehicles/{id:guid}",async (Guid id,CarDb db)=>{
 var v=await db.Vehicles.AsNoTracking().Where(v=>v.Id==id&&v.IsPublished&&v.IsApproved&&v.Status=="Available")
 .Select(v=>new {v.Id,v.Make,v.Model,v.Year,v.Price,v.ImageUrl,v.BranchId}).FirstOrDefaultAsync();
 return v==null?Results.NotFound():Results.Ok(v);
});
app.MapPost("/api/auth/login",async (LoginDto dto,CarDb db,PasswordHasher<AppUser> hasher)=>{
 var u=await db.Users.FirstOrDefaultAsync(x=>x.Username==dto.Username);
 if(u==null||hasher.VerifyHashedPassword(u,u.PasswordHash,dto.Password)==PasswordVerificationResult.Failed)return Results.Unauthorized();
 var claims=new[]{new Claim(ClaimTypes.NameIdentifier,u.Id.ToString()),new Claim("agency_id",u.AgencyId.ToString()),new Claim(ClaimTypes.Role,u.Role)};
 var token=new JwtSecurityToken("CarAgency","CarAgencyClients",claims,expires:DateTime.UtcNow.AddHours(4),signingCredentials:new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),SecurityAlgorithms.HmacSha256));
 return Results.Ok(new{accessToken=new JwtSecurityTokenHandler().WriteToken(token),expiresIn=14400});});
var api=app.MapGroup("/api").RequireAuthorization();
api.MapGet("/dashboard",async (ClaimsPrincipal user,CarDb db)=>{var a=Guid.Parse(user.FindFirst("agency_id")!.Value);return Results.Ok(new{vehicles=await db.Vehicles.CountAsync(x=>x.AgencyId==a),available=await db.Vehicles.CountAsync(x=>x.AgencyId==a&&x.Status=="Available"),customers=await db.Customers.CountAsync(x=>x.AgencyId==a),sales=await db.Sales.CountAsync(x=>x.AgencyId==a)});});
api.MapGet("/vehicles",async (ClaimsPrincipal user,CarDb db)=>{var a=Guid.Parse(user.FindFirst("agency_id")!.Value);return await db.Vehicles.Where(x=>x.AgencyId==a).OrderBy(x=>x.Make).ToListAsync();});
api.MapPatch("/vehicles/{id:guid}/publication",async (Guid id,PublicationDto dto,ClaimsPrincipal user,CarDb db)=>{
 var agency=Guid.Parse(user.FindFirst("agency_id")!.Value);
 var v=await db.Vehicles.FirstOrDefaultAsync(x=>x.Id==id&&x.AgencyId==agency);
 if(v==null)return Results.NotFound();
 v.IsPublished=dto.IsPublished;
 // Approval is separate and cannot be changed through publication endpoint.
 await db.SaveChangesAsync();return Results.Ok(new {v.Id,v.IsPublished,v.IsApproved});
}).RequireAuthorization("AdminOnly");
api.MapPatch("/vehicles/{id:guid}/approval",async (Guid id,ApprovalDto dto,ClaimsPrincipal user,CarDb db)=>{
 var agency=Guid.Parse(user.FindFirst("agency_id")!.Value);
 var v=await db.Vehicles.FirstOrDefaultAsync(x=>x.Id==id&&x.AgencyId==agency);
 if(v==null)return Results.NotFound();
 v.IsApproved=dto.IsApproved;await db.SaveChangesAsync();return Results.Ok(new {v.Id,v.IsPublished,v.IsApproved});
}).RequireAuthorization("AdminOnly");
api.MapPost("/vehicles",async (VehicleDto dto,ClaimsPrincipal user,CarDb db)=>{var a=Guid.Parse(user.FindFirst("agency_id")!.Value);if(!await db.Branches.AnyAsync(x=>x.Id==dto.BranchId&&x.AgencyId==a))return Results.BadRequest("Invalid branch");if(dto.Year<1900||dto.Price<0||string.IsNullOrWhiteSpace(dto.Vin))return Results.BadRequest("Invalid vehicle");var v=new Vehicle{AgencyId=a,BranchId=dto.BranchId,Vin=dto.Vin.Trim(),Make=dto.Make,Model=dto.Model,Year=dto.Year,Cost=dto.Cost,Price=dto.Price};db.Vehicles.Add(v);await db.SaveChangesAsync();return Results.Created($"/api/vehicles/{v.Id}",v);}).RequireAuthorization("AdminOnly");
api.MapGet("/customers",async (ClaimsPrincipal user,CarDb db)=>{var a=Guid.Parse(user.FindFirst("agency_id")!.Value);return await db.Customers.Where(x=>x.AgencyId==a).ToListAsync();});
api.MapPost("/customers",async (CustomerDto dto,ClaimsPrincipal user,CarDb db)=>{var a=Guid.Parse(user.FindFirst("agency_id")!.Value);var c=new Customer{AgencyId=a,FullName=dto.FullName,Phone=dto.Phone};db.Customers.Add(c);await db.SaveChangesAsync();return Results.Created($"/api/customers/{c.Id}",c);});
app.Run();
record LoginDto(string Username,string Password);
record VehicleDto(Guid BranchId,string Vin,string Make,string Model,int Year,decimal Cost,decimal Price);
record CustomerDto(string FullName,string Phone);

record PublicationDto(bool IsPublished);
record ApprovalDto(bool IsApproved);
