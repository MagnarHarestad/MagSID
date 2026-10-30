# The website's message wall

Visitors leave one line at the bottom of the website, pass a robot check, and it waits until you
approve it. Only then does it show on the site. GitHub Pages cannot receive or store anything, so this
small Cloudflare Worker does. It runs on Cloudflare's free plan.

`docs/index.html` keeps the wall hidden until `MESSAGES` near the end of that file has the Worker's
address and the robot check's site key.

## Setting it up

1. Make a free account at <https://dash.cloudflare.com/sign-up>.
2. In the dashboard, open **Turnstile → Add widget**, with the hostname `magnarharestad.github.io`. Keep
   the **site key** and the **secret key** it gives you.
3. Here, in this folder:

   ```bash
   npx wrangler login
   npx wrangler kv namespace create MESSAGES
   ```

   Put the id it prints into `wrangler.toml`.

4. Set the two secrets. Each asks you to paste the value, and it is never stored in this repository.
   Make the admin token long and random: it is the password to your approval page.

   ```bash
   npx wrangler secret put TURNSTILE_SECRET
   ```

   ```bash
   npx wrangler secret put ADMIN_TOKEN
   ```

5. Publish it:

   ```bash
   npx wrangler deploy
   ```

   It prints an address like `https://magsid-messages.<your-name>.workers.dev`.

6. Put that address and the **site key** into `MESSAGES` in `docs/index.html` and push. Neither is a
   secret. The wall appears on the website.

## Approving messages

Open `<the Worker's address>/admin` and enter the admin token. New messages wait there with **Approve** and
**Delete**, and what is on the website can be deleted from there too. Nothing tells you when one arrives,
so look in now and then. Up to 200 can wait; after that, visitors are asked to try again later.

## What it stores

The text and the date, nothing else. A visitor's address is only used, hashed, to allow one message a
minute, and that entry expires by itself. The robot check is Cloudflare Turnstile, which loads from
Cloudflare only when a visitor starts writing.

## Tests

```bash
node --test
```

Run them in this folder. They need Node 20 or later and nothing else: no Cloudflare account and no network.
