-- Records what Neovim learns from the terminal: every TermResponse it receives, its own
-- truecolor decision, and vim.tty.query (Neovim's XTGETTCAP client) for the table's names.
local out = assert(os.getenv('PROBE_OUT'))
local log = {}
local function add(s) log[#log + 1] = s end
local function esc(s)
  return (s:gsub('[%c\128-\255]', function(c) return string.format('\\x%02X', c:byte()) end))
end
add('nvim ' .. tostring(vim.version()))
vim.api.nvim_create_autocmd('TermResponse', { callback = function(ev)
  local seq = type(ev.data) == 'table' and ev.data.sequence or tostring(ev.data)
  add('TermResponse ' .. esc(seq))
end })
local batches = {
  { 'Tc', 'RGB', 'setrgbf', 'setrgbb', 'Su', 'Smulx', 'Setulc', 'Sync', 'Ss', 'Se', 'hs', 'tsl' },
  { 'fsl', 'Ms', 'Co', 'colors', 'TN', 'kcuu1' },
}
local results = {}
local function query(i)
  if i > #batches then return end
  require('vim.tty').query(batches[i], function(cap, found, seq)
    results[cap] = found and ('found ' .. esc(seq or '')) or 'not found'
  end)
  vim.defer_fn(function() query(i + 1) end, 1000)
end
vim.api.nvim_create_autocmd('UIEnter', { once = true, callback = function()
  vim.defer_fn(function()
    add('auto termguicolors=' .. tostring(vim.o.termguicolors))
    query(1)
    vim.defer_fn(function()
      for _, batch in ipairs(batches) do
        for _, n in ipairs(batch) do add(string.format('termcap %-8s %s', n, results[n] or 'no 1+r reply')) end
      end
      add('clipboard=' .. vim.inspect(vim.g.clipboard))
      vim.fn.writefile(log, out)
      vim.cmd('qa!')
    end, 3500)
  end, 2000)
end })
