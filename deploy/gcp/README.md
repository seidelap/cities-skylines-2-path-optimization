# CS2 cloud workstation (GCP), scale-to-near-zero

Cities: Skylines II is Windows-only and needs a GPU, so a Mac mini can neither play
it nor host the mod-development loop. This provisions a Windows + NVIDIA workstation
on GCP that you stream to the Mac, and that also serves as the build/test machine for
plan §5 steps 1–3.

**It is a development environment first.** Everything in this repo so far is validated
against a *synthetic* city. The point of standing this up is to run the real one
through the same harness — see [The calibration loop](#the-calibration-loop).

---

## Cost model (the honest version)

Three tiers. Compute genuinely scales to zero; storage does not, which is why
hibernate exists.

| state | what is billing | rough cost |
|---|---|---|
| **running** | VM + Windows licence + vWS licence | **~$1.20–1.60/hr** (verify for your region) |
| **stopped** (`cs2 down`) | boot disk 60 GB + game disk 150 GB | **~$20/mo**, i.e. ~$0.65/day |
| **hibernated** (`cs2 hibernate`) | one compressed snapshot + bucket | **~$2–4/mo** |

So: `down` between sessions in a week you're playing, `hibernate` when you're done for
a while. Ten hours of play in a month sits around $15–20 of compute plus a few dollars
of storage.

Two guards are installed on the VM because "I forgot to shut it down" is the only way
this gets expensive:

* **idle shutdown** — no Parsec session, no interactive login, quiet GPU for 20 minutes.
* **hard session cap** — unconditional shutdown after 6 hours of uptime. This is the
  backstop that survives a fooled idle detector. Do not disable it.

Optionally set `billing_account` to also get budget alerts at 50/90/100%.

> Prices drift and vary by region. Treat every figure here as "check the pricing
> calculator", not as a quote.

---

## Before you apply: two things that will block you

1. **GPU quota — the global gate, not the per-SKU ones.** On a fresh project the
   regional `NVIDIA_L4_GPUS` / `NVIDIA_T4_VWS_GPUS` quotas are *already* 1, and CPU
   and disk quotas are already ample. The single blocker is the **global**
   `GPUS_ALL_REGIONS`, which sits at 0 and vetoes all of them. Raise that one — see
   [`SESSION-RUNBOOK.md`](SESSION-RUNBOOK.md) §0 for the exact command. Measured
   twice: auto-approved in under two minutes, even on a brand-new project.

   Required APIs (Terraform will fail cryptically without them):
   ```
   gcloud services enable compute.googleapis.com storage.googleapis.com \
     iam.googleapis.com cloudresourcemanager.googleapis.com serviceusage.googleapis.com \
     cloudquotas.googleapis.com billingbudgets.googleapis.com --project PROJECT
   ```
   (Compute Engine + Cloud Storage + IAM + budgets is the whole surface — there is no
   Cloud Run, GKE, or serverless component in this deploy.)

2. **You are running on Windows Server, not Windows 11.** GCP does not offer desktop
   Windows. CS2 runs fine on Server 2022 with Desktop Experience, but two things are
   off by default and break streaming in confusing ways — the startup script fixes
   both (Windows Audio service, display sleep). If something feels haunted, read
   `C:\cs2\startup.log` first.

Also worth knowing: the vWS (NVIDIA RTX Virtual Workstation) licence is what makes
DirectX work. The `-vws` accelerator SKU covers the n1+T4 path; **L4/G2 licensing has
changed over time — verify it against current GCP docs before your first apply.**

---

## Setup

```bash
cd deploy/gcp
cp terraform.tfvars.example terraform.tfvars   # fill in project_id, zone, my_ip_cidr
terraform init && terraform apply

chmod +x cs2
./cs2 rdp        # mint a Windows password
./cs2 up         # start it, print the IP
```

RDP in once and do the three interactive things a script cannot do for you:

1. Sign into **Steam** (Steam Guard), set the library folder to `D:\Steam`, install CS2.
2. Sign into **Parsec**, enable hosting + start-on-boot.
3. Confirm `nvidia-smi` reports the GPU. If not, install the NVIDIA RTX Virtual
   Workstation driver manually and reboot.

From then on: `cs2 up` → connect with Parsec from the Mac → play or build → `cs2 down`
(or just walk away and let the idle guard do it).

Everything durable lives on `D:` (Steam, CS2, saves, the repo), so the VM itself is
disposable — hibernate destroys it and `cs2 wake` rebuilds it from the snapshot.

---

## The calibration loop

This is why the workstation is worth standing up at all.

```
                on the VM                      on your Mac / here
  ┌────────────────────────────┐        ┌──────────────────────────────┐
  │ CS2 + exporter mod         │        │ CS2Path.Harness              │
  │  captures lane graph,      │        │  import → replay real graph, │
  │  congestion trace, real    │ ─GCS─▶ │  real demand, real congestion│
  │  origin-destination trips  │        │  → A1 / A2 verdicts          │
  └────────────────────────────┘        └──────────────────────────────┘
```

On the VM:

```powershell
C:\cs2\build-mod.ps1                      # builds against the installed game, deploys to Mods
# ...run the game, trigger the export...
C:\cs2\upload-export.ps1 -Path D:\exports\mycity.cs2city
```

On the Mac:

```bash
./cs2 pull mycity.cs2city
dotnet run -c Release --project src/CS2Path.Harness -- import --file mycity.cs2city
```

That prints two verdicts, which are the two assumptions every performance number in
[`RESULTS.md`](../../RESULTS.md) currently rests on:

* **A1 — separator quality.** Elimination tree height and arc blow-up on a real map
  versus the synthetic 131k baseline (height 264, 9.2× arcs). Height is the number
  that drives query latency; if a real city is dramatically taller, the query story
  needs rework before anything else matters.
* **A2 — demand locality.** Cluster-cache hit rate on *recorded* trips versus the
  84.2% the synthetic zonal model produces. Uniform-random OD is the worst case for a
  WSPD-style cache; real commuting should beat it, but that is a claim, not a result.

You can exercise the whole loop before the in-game exporter exists:

```bash
dotnet run -c Release --project src/CS2Path.Harness -- export-synthetic --out synth.cs2city
dotnet run -c Release --project src/CS2Path.Harness -- import --file synth.cs2city
```

---

## What is deliberately not automated

* **Steam / Parsec sign-in** — accounts and 2FA are yours; a script that wanted them
  would be a script you shouldn't run.
* **Buying and installing CS2** — same reason.
* **Anything holding your credentials.** `cs2` shells out to your authenticated
  `gcloud`; this repo never stores a key. If you want a file pulled into an agent
  session, `cs2 share` mints a time-limited signed URL rather than opening the bucket.
