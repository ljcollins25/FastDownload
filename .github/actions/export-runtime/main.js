const fs = require('fs');
const out = ['ACTIONS_RUNTIME_TOKEN', 'ACTIONS_RESULTS_URL', 'ACTIONS_CACHE_URL']
  .filter(k => process.env[k]).map(k => k + '=' + process.env[k]).join('\n') + '\n';
fs.appendFileSync(process.env.GITHUB_ENV, out);
