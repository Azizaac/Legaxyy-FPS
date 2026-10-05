# SpaceX Interactive Website — Development Plan & Implementation Guide

> Note: this workspace (`Legaxyy-FPS`) is a .NET overlay app, unrelated to the web project. The plan below is a standalone greenfield blueprint you can drop into a new repo.

---

## 1. Executive Summary

Build an immersive, performance-first marketing/engagement platform for SpaceX centered on an interactive 3D homepage. The experience must feel futuristic and cinematic while remaining accessible, fast on mid-range hardware, and easy for a content team to update (missions, launches, news).

**Guiding principles**
1. **Performance is a feature** — target 60 FPS on desktop, 30+ FPS on mobile; never trade usability for spectacle.
2. **Progressive enhancement** — 2D/fallback hero for unsupported or low-power devices.
3. **Brand fidelity** — dark, high-contrast, technical aesthetic; precise typography; restrained motion.
4. **Accessibility first** — WCAG 2.2 AA, keyboard-navigable, reduced-motion support.
5. **Content velocity** — heads-up CMS so non-engineers publish missions/news in minutes.

---

## 2. Information Architecture & Site Structure

```
/                       Home (3D interactive hero + live launch ticker)
/about                  Company, history, leadership, facilities
/missions               Mission list (upcoming / past) + filters
/missions/:slug         Mission detail (timeline, vehicle, payload, webcast)
/technology             Vehicles, engines, reusability, Starship, Starlink
/technology/:slug       Deep-dive per technology
/gallery                Media grid (images, video, 3D model viewer)
/contact                Form, HQ info, press/careers links
/news                   Dynamic articles (stretch tab, or fold into /about)
```

**Navigation**
- Fixed translucent top bar; logo left, primary tabs center/right; CTA "Watch Latest Launch".
- Mega-menu on hover/focus for Missions & Technology (keyboard + touch friendly).
- Persistent footer: sitemap, social, legal (privacy/terms), newsletter, language switcher.
- Mobile: full-screen drawer, thumb-reachable, 44px min tap targets.

---

## 3. Design System & UI/UX

### 3.1 Visual language
- **Palette:** near-black `#0A0A0B`, graphite `#141416`, SpaceX white `#F5F5F7`, accent electric blue `#2C6BFF` (or brand-appropriate), signal amber for LIVE states.
- **Typography:** condensed geometric sans for headings (e.g., "D-DIN"-like / *Saira*, *Barlow Condensed*), neutral sans for body (*Inter*).
- **Grid:** 12-col desktop, 8-col tablet, 4-col mobile; 8px spacing scale.
- **Motion:** 150–300ms easing for UI; scroll-driven cinematic transitions on hero; honor `prefers-reduced-motion`.

### 3.2 UX rules
- Above the fold: headline + primary CTA + live launch countdown in ≤ 1s.
- Max 2 clicks to any critical info (next launch, vehicle specs, contact).
- Skeleton loaders for dynamic sections; never block the 3D canvas on network.
- Visible focus rings; logical tab order; ARIA labels on all interactive 3D controls.

---

## 4. Front-End Development

### 4.1 Stack
| Concern | Recommendation | Rationale |
|---|---|---|
| Framework | **Next.js 14 (App Router) + TypeScript** | SSR/ISR for SEO, image optimization, route splitting |
| Styling | **Tailwind CSS** + CSS Modules for rare custom cases | Velocity + consistency |
| 3D | **Three.js** via **React Three Fiber (R3F)** + **drei** | Declarative, tree-shakeable, React-native ecosystem |
| State | Zustand (client), TanStack Query (server data) | Lightweight |
| Animation | Framer Motion (UI) + GSAP ScrollTrigger (cinematic) | Timelines & scroll orchestration |
| Content | MDX for static pages; CMS-driven JSON for dynamic | Fast, editable |
| i18n | next-intl | Future localization |

### 4.2 3D homepage — implementation
**Assets**
- Models: `glTF 2.0` / `.glb`, Draco or Meshopt compressed, KTX2/Basis textures, LOD variants.
- Curated set: Falcon 9 (with deployed grid fins), Starship + Super Heavy, Crew Dragon, Starman/Roadster Easter egg, Earth (globe), orbital tracer.
- Source scientifically accurate geometry from public/Creative Commons models; validate proportions against published specs; label as *stylized artistic representation* where not exact.

**Scene architecture**
- `Canvas` with `<Suspense>` boundaries; critical UI (nav, CTAs) is DOM overlay **on top of** the canvas.
- Post-processing (bloom, subtle chromatic aberration) via `@react-three/postprocessing` — toggled off on low-end.
- Camera: OrbitControls constrained (restrict polar/zoom), plus a scroll-driven spline camera path between "scenes" (LEO → Mars transfer → launch pad).
- Lighting: HDR environment map + directional key light; physically based materials (`MeshStandardMaterial`/`MeshPhysicalMaterial`).
- Hotspots: clickable annotations that open spec cards (payload, thrust, height) with accessible text equivalents.

**Performance strategy**
- `useGLTF.preload()` behind an initial loading screen with progress.
- Instancing for particles/debris; instanced star field.
- Adaptive `dpr` (`[1, 2]`), dynamic resolution scaling based on FPS monitor.
- Render on demand when scene is static; pause when tab hidden / offscreen (`IntersectionObserver`).
- Detect capability (`WebGL2`, `deviceMemory`, `hardwareConcurrency`, `navigator.connection.saveData`) → serve **3 quality tiers** (high/med/hero-image fallback).
- Budgets: hero JS ≤ 350KB gzip (3D libs lazy-loaded), LCP ≤ 2.5s, TBT ≤ 200ms.

### 4.3 Responsiveness & compatibility
- Fluid type with `clamp()`; mobile hero uses lower-poly model + reduced particle count.
- Touch: single-finger orbit, two-finger zoom, tap-to-focus hotspots.
- Support target: evergreen Chrome/Edge/Firefox/Safari 16+, iOS Safari 16+, Android Chrome; graceful fallback for older.
- Test matrix via BrowserStack or Playwright projects.

---

## 5. Back-End Development

### 5.1 Recommended stack
| Concern | Option A (recommended) | Option B |
|---|---|---|
| API | **Node.js (NestJS) + TypeScript** | Python FastAPI/Django REST |
| CMS | **Strapi / Payload / Sanity** | Contentful (SaaS) |
| DB | **PostgreSQL** (+ Redis cache) | MongoDB if highly schemaless |
| Media | S3-compatible object storage + CDN | Cloudinary |
| Search | Postgres FTS → Meilisearch if needed | Algolia |
| Auth (admin) | OAuth/SSO + role-based access | — |
| Realtime | WebSocket/SSE for launch countdown & live status | — |

### 5.2 API surface (REST or GraphQL; version under `/api/v1`)
- `GET /missions?status=upcoming|past&vehicle=&limit=` — paginated
- `GET /missions/:slug` — detail incl. timeline, media, webcast URL
- `GET /technology`, `GET /technology/:slug`
- `GET /news?limit=&cursor=` — cursor pagination
- `GET /launches/next` — next launch metadata (countdown-synced)
- `POST /contact` — rate-limited, spam-protected
- `GET /media?collection=gallery&page=` — CDN-backed
- `GET /health` — liveness/readiness for orchestrator

### 5.3 Database schema (PostgreSQL, core tables)
```
missions(id, slug UNIQUE, name, status ENUM, launch_at TIMESTAMPTZ,
         vehicle_id FK, payload, description, webcast_url, outcome,
         created_at, updated_at)
vehicles(id, slug, name, height_m, thrust_kN, payload_leo_kg,
         reusable BOOL, specs JSONB, model_url)
technologies(id, slug, title, category, body_md, hero_media_id)
news(id, slug, title, excerpt, body_md, author, published_at, tags[])
media(id, url, type ENUM[image,video,model], alt_text, credit, width, height)
mission_media(mission_id, media_id, sort)
contact_submissions(id, name, email, topic, message, ip_hash, created_at)
users(id, email, role ENUM[admin,editor,viewer], password_hash)
```
- `specs JSONB` for flexible per-vehicle figures (avoid schema churn).
- Indexes on `missions.status`, `missions.launch_at`, FTS index on `news(title, body)`.
- Migrations via Prisma/Knex/Alembic; seed scripts for dev.

### 5.4 Dynamic content pipeline
- CMS webhooks → `POST /api/revalidate` triggers Next.js **ISR** revalidation of affected routes.
- Next launch endpoint cached 30s at edge; countdown computed **client-side** from server timestamp to avoid clock drift.
- News/Missions rendered with ISR (e.g., `revalidate: 60`) → near-static speed, fresh content.
- Image/video transforms on the fly (AVIF/WebP, responsive `srcset`) via CDN.

---

## 6. Security & Performance

### 6.1 Security
- HTTPS/HSTS, TLS 1.3; secure cookies (`HttpOnly`, `Secure`, `SameSite`).
- Input validation (Zod/Pydantic) on every endpoint; parameterized queries / ORM.
- Rate limiting (edge + app), CAPTCHA/Turnstile + honeypot on contact form.
- CSP, `X-Content-Type-Options`, `Referrer-Policy`, `Permissions-Policy`; sanitize CMS markdown HTML.
- Admin: SSO/MFA, least-privilege roles, audit log; secrets in vault/env, never in repo.
- Dependency scanning (Dependabot/Snyk), SAST in CI; OWASP Top 10 checklist.

### 6.2 Performance
- CDN for all static + 3D assets with immutable hashing; Brotli compression.
- Code-split by route; lazy-load 3D; prefetch likely next route.
- Redis caching for hot API reads; DB connection pooling (PgBouncer).
- Image optimization pipeline; subset fonts; preconnect to CDN/HDR host.
- Monitor Core Web Vitals (LCP/INP/CLS) via RUM (Vercel Analytics/Sentry).

---

## 7. Accessibility & Cross-Device Compatibility

- 3D canvas is decorative-by-default with an **accessible alternative**: text summary, static renders, and spec tables for every model.
- Full keyboard operation for nav and model controls; focus trapping in modals.
- `prefers-reduced-motion` → disable camera flythroughs, keep static hero.
- Contrast ≥ 4.5:1 body / 3:1 large; never rely on color alone for status.
- Test: axe-core + Lighthouse a11y CI gates; manual screen-reader passes (NVDA/VoiceOver).
- Device matrix: desktop, tablet, phone, low-end Android; orientation changes; safe-area insets.

---

## 8. Testing & QA

- **Unit:** Vitest/Jest (utils, API handlers, hooks).
- **Component:** React Testing Library.
- **E2E:** Playwright across Chromium/Firefox/WebKit; visual regression with snapshots.
- **3D:** perf budget checks in CI (frame time, model size, draw calls); manual device lab.
- **API:** integration tests + contract tests (OpenAPI); load test (k6) for launch-day traffic spikes.
- **CI gates:** lint (ESLint), typecheck (tsc), a11y, Lighthouse thresholds.

---

## 9. Deployment & Operations

- **Hosting:** Vercel (Next.js) or containerized on AWS/GCP (ECS/Cloud Run) + CloudFront.
- **Infra as code:** Terraform; separate `dev`/`staging`/`prod` environments.
- **CI/CD:** GitHub Actions → lint/test/build → preview deploys → gated prod release.
- **Observability:** Sentry (errors incl. WebGL context loss), structured logs, uptime + synthetic checks, Web Vitals dashboards.
- **Scalability:** stateless API behind load balancer, horizontal autoscaling, read replicas, CDN absorbs traffic surges (e.g., launch days).
- **Resilience:** feature flags to disable 3D / heavy effects under load; graceful degradation to 2D hero.

---

## 10. Phased Roadmap

| Phase | Scope | Duration |
|---|---|---|
| 0 | Discovery, design system, content audit | 1–2 wks |
| 1 | Core 2D site (About/Missions/Technology/Gallery/Contact) + CMS | 4–6 wks |
| 2 | 3D hero (Falcon 9, Earth, hotspots) + quality tiers | 3–4 wks |
| 3 | Dynamic live launch countdown, news pipeline, webhooks | 2 wks |
| 4 | Accessibility, performance hardening, cross-browser QA | 2 wks |
| 5 | Launch, RUM monitoring, iteration | ongoing |

**Definition of done:** LCP ≤ 2.5s, INP ≤ 200ms, CLS ≤ 0.1, 60/30 FPS tiers, axe clean, all tabs functional, content editable without redeploy.

---

## 11. Implementation Status

A working foundation of this plan lives in `spacex-website/`. Deviations from the document above are noted.

### Delivered
- **Framework:** Next.js 16 (App Router, Turbopack) + React 19 + TypeScript + Tailwind CSS v4 (scaffold resolved to Next 16 rather than the planned Next 14; all APIs use the Next 16 async `params`/`searchParams` model).
- **3D homepage:** React Three Fiber + drei. Procedurally generated Falcon 9 / Starship / Dragon (no external asset dependency), clickable component hotspots with accessible spec cards, procedurally shaded Earth with atmosphere, animated starfield, orbit/zoom controls.
- **Performance tiers:** runtime capability detection → high/medium/low profiles (star count, sphere segments, DPR, atmosphere toggle). Static SVG fallback with opt-in for no-WebGL / reduced-motion users.
- **Pages:** About, Missions (vehicle filter), Mission detail, Technology, Technology detail, Gallery, News, News detail, Contact, custom 404.
- **API:** `/api/missions`, `/api/missions/[slug]`, `/api/launches/next` (with `serverTime`), `/api/news`, `/api/contact` (Zod + rate limit + honeypot), `/api/revalidate` (ISR webhook).
- **SEO / security:** per-route metadata, sitemap, robots, Open Graph, security headers.
- **Verified:** `next typegen`, `tsc --noEmit`, `eslint`, and `next build` (32 routes) all pass; production server smoke test returned 200s and working API responses.

### Deferred (matching the roadmap)
- Persistent PostgreSQL + Prisma and a headless CMS (seed data in `src/lib/data.ts` is the swap point).
- `@react-three/postprocessing` bloom/vignette (currently CSS-based glow).
- Playwright/Vitest suites, Lighthouse CI budgets, RUM/monitoring.
- Full CMS webhook wiring and authenticated admin.
