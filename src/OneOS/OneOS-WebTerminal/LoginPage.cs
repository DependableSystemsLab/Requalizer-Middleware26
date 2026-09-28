using System.Net;

namespace OneOS.WebTerminal
{
    // The sign-in page (from the V5B server.js). It posts username, password and the page to return to.
    public static class LoginPage
    {
        public static string Render(string? error = null, string? next = null) => $$"""
<!DOCTYPE html>
<html lang="en">
	<head>
		<base href="/">
		<meta charset="utf-8">
		<meta name="viewport" content="width=device-width, initial-scale=1">
		<title>OneOS Web Terminal | Login</title>
		<style>
body {
	background: linear-gradient(-45deg, #99082a, #990866, #3a0775, #074275);
	background-size: 400% 400%;
	animation: gradient 20s ease infinite;
	height: 100vh;
}

@keyframes gradient {
	0% { background-position: 0% 50%; }
	50% { background-position: 100% 50%; }
	100% { background-position: 0% 50%; }
}

label { display:block; font-size:small; }
input { padding: 0.5em; border: 1px solid white; background: none; }
button { padding: 1em; border: 1px solid white; background: none; }
button:hover { box-shadow: 0 0 5px #fff8; background: #fff8; }
		</style>
	</head>
	<body style="padding:0; margin:0; font-family: Helvetica, sans-serif">
		<div style="position:absolute; width:100%; height:100%; align-items:center; justify-content:center; display:flex; flex-direction: column;">
			<form action="/login" method="POST" style="padding:2em; box-shadow:0 0 10px #0008; background: #fffa">
				<input type="hidden" name="next" value="{{WebUtility.HtmlEncode(next ?? "/")}}"/>
				<div style="padding: 0.5em;">
					<label>Username</label>
					<input type="text" name="username" autocomplete="username"/>
				</div>
				<div style="padding: 0.5em;">
					<label>Password</label>
					<input type="password" name="password" autocomplete="current-password"/>
				</div>
				{{(error != null ? $"<div style=\"margin:0.5em; padding:0.5em; background:#f88; color:white; border-radius:0.25em;\">{WebUtility.HtmlEncode(error)}</div>" : "")}}
				<div style="text-align:center; padding: 0.5em;">
					<button type="submit">Log in</button>
				</div>
			</form>
		</div>
		<script>
		document.querySelector('input[name=username]').focus();
		</script>
	</body>
</html>
""";

        // `next` if it is a path on this server, otherwise "/" (no open redirects).
        public static string SafeNext(string? next) =>
            !string.IsNullOrEmpty(next) && next.StartsWith('/') && !next.StartsWith("//") && !next.StartsWith("/\\") && !next.StartsWith("/login")
                ? next : "/";
    }
}
