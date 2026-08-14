# Setting up the week, step by step

This guide assumes you have never used Docker, Tailscale or a terminal. It
starts from an empty Windows 11 laptop and ends with both of you adding things
from your phones. Every command is written out in full — you can copy and paste
each one.

Set aside about **45 minutes**. Most of it is waiting for downloads.

[DEPLOY.md](./DEPLOY.md) is the same thing written for someone who already knows
this stack. If a step here seems laborious, that is the one to read instead.

---

## What you are actually building

The laptop becomes a small private server. It runs the app, keeps the couple's
data, and never puts any of it on the public internet.

Your phones reach it through **Tailscale**, which builds a private network
between devices you own. Think of it as a cable running from each phone to the
laptop — one that only your devices can plug into. Nobody else can reach the
laptop, even though your phone can reach it from anywhere with a signal.

Four pieces of software are involved, and you only ever interact with the last:

| | What it is |
|---|---|
| **Git** | Downloads the code from GitHub and keeps it up to date. |
| **Docker** | Runs the app and its database in sealed boxes called *containers*, so nothing has to be installed on Windows itself. |
| **Tailscale** | The private network between your laptop and your phones. |
| **Couple OS** | The app. |

One thing that is deliberately unusual: **there are no passwords anywhere**. You
sign in by entering your email address and clicking a link. That is the whole
system.

---

## Before you start

You need:

- **A Windows 11 laptop that can stay plugged in and switched on for seven
  days.** It does not need to be powerful. It does need to be somewhere it will
  not be closed, unplugged or taken to work.
- **A GitHub account**, if the repository is private — you will be asked to sign
  in when you download the code.
- **An email address for each of you.** They do not need to be real inboxes that
  you can check. Read the [warning about email](#a-warning-about-the-email-part)
  before you decide which addresses to use.
- **About 10 GB of free disk space.**

---

## Step 1 · Install the three programs

Install all three, then restart the laptop once at the end.

### Git

Download from **https://git-scm.com/download/win**. The download starts by
itself. Run the installer and click Next through every screen — the defaults are
correct.

### Docker Desktop

Download from **https://www.docker.com/products/docker-desktop/** and run the
installer. When it offers **"Use WSL 2 instead of Hyper-V"**, leave it ticked.

Docker needs a setting called virtualization switched on in your laptop's
firmware. It usually already is. If Docker Desktop complains about it on first
start, search the web for "enable virtualization" plus your laptop's brand —
it is a setting you toggle in a menu before Windows boots, and it is the one
thing in this guide you may need to look up.

### Tailscale

Download from **https://tailscale.com/download/windows** and run the installer.

### Then restart

Restart the laptop. Docker Desktop needs it.

After restarting, **open Docker Desktop** and leave it running. It shows a
whale icon in the system tray, near the clock. Click the gear icon and make sure
**"Start Docker Desktop when you sign in to your computer"** is ticked.

> **The most important thing in this guide.** Docker only runs while you are
> signed in to Windows. If you sign out, everything stops and both phones lose
> the app. Lock the screen instead — hold the **Windows key** and press **L**.
> That leaves everything running. Never choose Sign out or Shut down.

---

## Step 2 · Download the code

Open **PowerShell**: click Start, type `powershell`, press Enter. A blue window
opens. This is where every command in this guide is typed.

Copy this line, paste it into PowerShell with a right-click, and press Enter:

```powershell
git clone https://github.com/aman-liquid-nitro/couple-os.git C:\CoupleOS
```

If a GitHub sign-in window appears, sign in. That happens once.

`C:\CoupleOS` is deliberately not inside Documents or Desktop — those are often
synced to OneDrive, and OneDrive tries to upload files while Docker is writing
to them, which breaks both.

Now move into that folder. **Every remaining command in this guide assumes you
have done this**, so if you close PowerShell and open it again, run this first:

```powershell
cd C:\CoupleOS
```

---

## Step 3 · Get a key for the AI

The app sends each note you write to an AI model, which decides that "we're out
of milk" is a shopping item. The model runs on Ollama's servers rather than your
laptop, because a laptop running one for a week would be unusably slow and hot.

Go to **https://ollama.com**, create a free account, then go to
**https://ollama.com/settings/keys** and create a key. It looks like a long line
of random characters.

**Copy it somewhere you can paste from in a minute.** The website will not show
it to you again.

Ollama's stated policy is that prompts are never logged or trained on, which is
why it is the one outside service this project uses. Your notes do leave the
laptop to be read by that model. Nothing else does.

---

## Step 4 · Set up the private network

### On the laptop

Open Tailscale from the system tray and sign in. Use whichever account you like
— Google, Microsoft, GitHub — but **remember which one**, because both phones
must sign in to the same one.

### In the Tailscale website

Go to **https://login.tailscale.com/admin/dns** and turn on two things:

1. **MagicDNS** — gives your laptop a memorable name instead of a number.
2. **HTTPS Certificates** — the padlock in the browser. Without this the phones
   will refuse to sign in, and the reason will not be obvious.

### Find your laptop's name

Back in PowerShell:

```powershell
tailscale status
```

The first line is your laptop. Its name looks like
`my-laptop.tail1234.ts.net`. **Write it down** — you need it twice in the next
step and on both phones.

If PowerShell says `tailscale` is not recognised, close PowerShell, open it
again, and retry. The installer adds it to the list of known commands, and only
new windows pick that up.

---

## Step 5 · Write the settings file

The app reads its settings from a file called `.env`. Make your own copy of the
example:

```powershell
Copy-Item .env.example .env
```

Open it in Notepad:

```powershell
notepad .env
```

Find each of these lines and change it. They are scattered through the file, and
each one already has an explanation above it. Use **Ctrl+F** to find them.

| Find this line | Change it to |
|---|---|
| `BIND_ADDRESS=0.0.0.0` | `BIND_ADDRESS=127.0.0.1` |
| `PUBLIC_BASE_URL=` | `PUBLIC_BASE_URL=https://my-laptop.tail1234.ts.net` |
| `OLLAMA_BASE_URL=http://host.docker.internal:11434` | `OLLAMA_BASE_URL=https://ollama.com` |
| `OLLAMA_API_KEY=` | `OLLAMA_API_KEY=` then paste your key |
| `LLM_FAST_MODEL=qwen3.5:4b` | `LLM_FAST_MODEL=gemma4:31b` |
| `LLM_DEEP_MODEL=qwen3.5:4b` | `LLM_DEEP_MODEL=gemma4:31b` |

Use **your own** laptop name from Step 4, not `my-laptop.tail1234.ts.net`. Start
it with `https://` and put nothing after `.ts.net` — no slash.

Save with **Ctrl+S** and close Notepad.

`BIND_ADDRESS=127.0.0.1` is the smallest-looking line and the one that matters
most: it tells Docker to keep everything on the laptop itself, so Tailscale is
the only way in. Left as it was, the database would accept connections from
anyone on the same café wifi.

---

## Step 6 · Start it

```powershell
docker compose -f docker-compose.yml -f docker-compose.live.yml up -d --build
```

The first run takes **five to fifteen minutes** and prints a great deal of text.
It is downloading and assembling everything. Later runs take seconds.

If it stops immediately with a message about `PUBLIC_BASE_URL` or
`OLLAMA_API_KEY`, one of those is still empty in `.env`. That check is
deliberate — both failures are otherwise invisible, and you would find out days
later.

Check it worked:

```powershell
docker compose ps
```

You want three lines, each saying **healthy**:

```text
api       Up 26 seconds (healthy)
db        Up 31 seconds (healthy)
maildev   Up 31 seconds (healthy)
```

If `api` says `starting`, wait thirty seconds and run it again. `healthy` here
means it genuinely reached its database — not just that something is running.

---

## Step 7 · Put it on the private network

Two commands. The first publishes the app, the second publishes the mail page
you will collect sign-in links from.

```powershell
tailscale serve --bg 8080
```

```powershell
tailscale serve --bg --https=8443 1080
```

If either says permission denied, close PowerShell, right-click the PowerShell
icon, choose **Run as administrator**, `cd C:\CoupleOS` again, and retry.

Check both are listed:

```powershell
tailscale serve status
```

Now open a browser **on the laptop** and go to your address —
`https://my-laptop.tail1234.ts.net`. You should see a sign-in page. The very
first visit can take ten seconds while a certificate is issued.

**Do not sign in yet.** Do Step 8 first, while you are still at the laptop.

---

## Step 8 · Stop the laptop going to sleep

This is the step people skip and regret. A sleeping laptop is an app that is
down, and you will not find out until someone tries to add something at a bus
stop.

There are five settings, and all five matter.

### 1 · Never sleep

Start → **Settings** → **System** → **Power & battery** → **Screen and sleep**.

Set **"When plugged in, put my device to sleep after"** to **Never**.

The screen turning off is fine and saves power — only sleep matters.

### 2 · Closing the lid does nothing

Press the Windows key, type `Choose what closing the lid does`, press Enter.

In the **Plugged in** column, set **"When I close the lid"** to **Do nothing**.
Click **Save changes**.

Now you can close the lid and leave it on a shelf.

### 3 · Windows must not restart itself

Start → **Settings** → **Windows Update** → **Pause updates**. Pause for **4
weeks**.

Windows installs updates and restarts overnight without asking. If it does that
mid-week, everything comes back only when you next sign in — and the containers
will be waiting, but you will not know they were down.

### 4 · The wifi must not switch itself off

Press the Windows key, type `Device Manager`, press Enter. Expand **Network
adapters**. Find the one with "Wi-Fi" or "Wireless" in its name, right-click it,
choose **Properties**, then the **Power Management** tab.

Untick **"Allow the computer to turn off this device to save power"**. Click OK.

### 5 · Leave it plugged in

On battery, Windows ignores several of the settings above.

### Then lock it, do not sign out

Hold the **Windows key** and press **L**. The laptop is now serving both of you
and can be left alone.

---

## Step 9 · Get it onto the phones

Do this on **both** phones. It is the same on iPhone and Android apart from
where the buttons are.

### 1 · Install Tailscale

- **iPhone:** App Store, search Tailscale, install.
- **Android:** Play Store, search Tailscale, install.

### 2 · Sign in with the same account as the laptop

This is the step people get wrong. If the phone signs in to a different account,
it joins a different private network and the address will not load.

### 3 · Turn it on

- **iPhone:** tap **Connect**. iOS asks to add a VPN configuration — allow it,
  and confirm with Face ID or your passcode. This is normal: Tailscale works as
  a VPN, and iOS asks about all of them.
- **Android:** tap the toggle and accept the connection request.

Leave Tailscale connected for the week. It uses very little battery when idle.

### 4 · Open the app

Open Safari (iPhone) or Chrome (Android) and go to your address:

```text
https://my-laptop.tail1234.ts.net
```

You should see the sign-in page. If you do not, see
[When something is wrong](#when-something-is-wrong).

### 5 · Put it on the home screen

This makes it open like an app rather than a browser tab, which matters more
than it sounds — the whole point of the week is finding out whether people
capture a thought in the ten seconds they have.

- **iPhone:** tap the **Share** button (a square with an arrow), scroll down,
  tap **Add to Home Screen**, tap **Add**.
- **Android:** tap the **⋮** menu, tap **Add to Home screen**, tap **Add**.

---

## Step 10 · Sign in and pair up

### A warning about the email part

Sign-in links are not sent to a real inbox. They land on a page called maildev
that lives on the laptop, at
`https://my-laptop.tail1234.ts.net:8443`, and **that page shows everyone's
messages to anyone who opens it**.

For the two of you on your own private network, that is a reasonable trade for a
week — it means no email account to set up and nothing to go wrong on day one.
But be clear about what it means: either of you can open that page and use the
other's sign-in link. It is not private from each other. It stays acceptable
only as long as no third device joins your Tailscale network.

### First partner

1. Open the app and enter your email address. Tap **Email me a link**.
2. Open a new tab at `https://my-laptop.tail1234.ts.net:8443`.
3. Click the newest message. Click the link inside it.
4. You are signed in. It asks what to call the couple — type anything, or leave
   it — and tap **Create**.
5. It then asks for your partner's email address. Enter it and tap **Send
   invitation**.

### Second partner

1. On the other phone, open `https://my-laptop.tail1234.ts.net:8443`.
2. Find the invitation, click the link inside it.
3. That is the whole sign-up. You are both now in the same couple.

Links can be used once and expire. If one stops working, ask for another from
the sign-in page.

### What you are looking at

- **shared.md** — the home page, and a file you both write in. Type lines into
  the big box, then press **Process**. The app reads each line, turns it into
  shopping items, tasks, reminders, events, expenses or memories, and tells you
  what it did. Lines it handled move to a *Processed* section; anything it could
  not do stays where it is with the reason.
- **The one-line box at the top** — type one thing, press Enter, done. No
  Process, no waiting. This is the doorway version, and the week is largely a
  test of whether you use it.
- **Private** — a chat only you can see. Your partner cannot see it on any
  screen. This is where a surprise goes.
- **Captured** — everything the app has recorded, grouped by kind.
- **Partner** — invite, or check who is in the couple.

---

## Every day · Back it up

The laptop holds the only copy of everything. Run these two on the laptop once a
day. They take a second.

Make the folder once:

```powershell
mkdir C:\CoupleOS\backups
```

Then, daily — the first saves the database, the second saves uploaded photos and
receipts, which are stored separately and would not be in the first:

```powershell
docker compose exec -T db pg_dump -U postgres -f /tmp/dump.sql coupleos; docker cp coupleos-db:/tmp/dump.sql "C:\CoupleOS\backups\db-$(Get-Date -Format yyyy-MM-dd).sql"
```

```powershell
docker run --rm -v couple-os_coupleos-attachments:/data -v "C:/CoupleOS/backups:/out" alpine tar czf /out/files-$(Get-Date -Format yyyy-MM-dd).tar.gz -C /data .
```

Copy the `backups` folder to a USB stick or another computer at least once. As
written, these files sit on the same disk as the thing they are protecting,
which saves you from a mistake but not from a dead drive.

---

## When something is wrong

| What you see | What to do |
|---|---|
| The address does not load on a phone | Check Tailscale is connected on the phone, and that it is signed in to the **same account** as the laptop. This is the usual cause. |
| It loads on the laptop but not the phone | Run `tailscale serve status` on the laptop. If it is empty, redo Step 7. |
| A browser warning about the certificate | HTTPS Certificates is not switched on. Go back to Step 4 and enable it in the Tailscale website. |
| Everything stopped at once | The laptop slept, restarted, or you signed out of Windows. Sign back in, open Docker Desktop, wait a minute, then run `docker compose ps`. |
| **Process** gives an error about the model | The laptop lost internet, or the key in `.env` is wrong. Check the laptop can load a website. |
| Sign-in link does not work | They can be used once. Ask for a new one from the sign-in page. |
| You want to see what went wrong | `docker compose logs api --tail 50` |

To start everything again after a restart:

```powershell
cd C:\CoupleOS; docker compose -f docker-compose.yml -f docker-compose.live.yml up -d
```

---

## At the end of the week

Stop it without deleting anything:

```powershell
cd C:\CoupleOS; docker compose -f docker-compose.yml -f docker-compose.live.yml down
```

Take it off the private network:

```powershell
tailscale serve --https=443 off; tailscale serve --https=8443 off
```

Turn Windows Update back on: Settings → Windows Update → **Resume updates**.

> **The one command to never run** is `docker compose down -v`. The `-v` deletes
> the database — everything either of you wrote. There is no undo, and the only
> way back is the backups above.

Then answer the five questions under **Definition of validated** in
[V0_SCOPE.md](./V0_SCOPE.md). They are what the week was for.
