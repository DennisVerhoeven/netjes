// Exits before it is ready, after output in terminal colours: what Vite logs for a page with a syntax error, and a
// line of the app's own.
console.log('\u001b[32mstarting\u001b[39m');
console.error('\u001b[31m[PARSE_ERROR] \u001b[0mExpected `>` but found `<`');
console.error('    \u001b[38;5;246m╭\u001b[0m\u001b[38;5;246m─\u001b[0m\u001b[38;5;246m[\u001b[0m app/about/page.tsx:10:7 \u001b[38;5;246m]\u001b[0m');
process.exit(1);
